using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Drives the notification dispatch loop. Kept minimal — the
    /// <c>cf-episode-notifier</c> Cloudflare Worker is the source of
    /// timing (cron triggers, KV-tracked dedup), and this service just
    /// runs the refresh + dispatch pass on each <c>TriggerRefreshAsync</c>
    /// call. The <see cref="BackgroundService"/> startup + UTC-midnight
    /// loop is the only in-process timer — it refreshes the schedule
    /// cache + dispatches any past-window airings (catches notifications
    /// the Worker may have failed to deliver), nothing more.
    /// <para>
    /// Dispatcher idempotency lives in <see cref="INotificationStore.CreateAsync"/>'s
    /// <c>INSERT OR IGNORE</c> against the <c>(uid, anime_id, season,
    /// episode_number)</c> unique index. Re-running the dispatch loop
    /// for an already-notified airing is cheap (per-user no-ops) so we
    /// don't need a separate per-airing notified flag.
    /// </para>
    /// </summary>
    public class EpisodeNotificationScheduler : BackgroundService, IEpisodeNotificationScheduler
    {
        // Airings within this lookback window get dispatched on each
        // RefreshAndDispatchAsync pass. 1h covers the worker's typical
        // ping interval + small jitter; longer would re-walk more
        // already-handled airings on every call without benefit (the
        // notifications UNIQUE INDEX makes them no-ops, but iteration
        // isn't free).
        private static readonly TimeSpan DispatchLookback = TimeSpan.FromHours(1);

        // Bounded refresh batch on startup so we don't fan out to every
        // user's tracker at the same moment after a redeploy.
        private const int StartupRefreshBatch = 50;

        private readonly IServiceProvider _services;
        private readonly ILogger<EpisodeNotificationScheduler> _logger;

        public EpisodeNotificationScheduler(IServiceProvider services, ILogger<EpisodeNotificationScheduler> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Startup pass — refresh the schedule + dispatch any past-
            // window airings (covers anything the Worker pinged for
            // while the process was down, plus the first-time-after-
            // redeploy case where no Worker ping has fired yet).
            await RefreshAndDispatchAsync(stoppingToken);
            await BackstopRefreshWatchingCachesAsync(stoppingToken);

            // Loop: sleep until next UTC midnight, then refresh +
            // dispatch. The daily cadence is the rhythm at which
            // AniList's published schedule moves; intra-day dispatch is
            // the Worker's job.
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var nextMidnight = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
                try { await Task.Delay(nextMidnight - now, stoppingToken); }
                catch (OperationCanceledException) { break; }

                await RefreshAndDispatchAsync(stoppingToken);
                await BackstopRefreshWatchingCachesAsync(stoppingToken);
            }
        }

        /// <summary>
        /// External-trigger entry point. <c>POST /api/v1/cron/check-releases</c>
        /// (called by the cf-episode-notifier Worker every minute that has an
        /// airing due) hits this; refreshes the schedule + dispatches any
        /// past-lookback-window airings.
        /// </summary>
        public Task TriggerRefreshAsync(CancellationToken ct = default)
            => RefreshAndDispatchAsync(ct);

        private async Task RefreshAndDispatchAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _services.CreateScope();
                var schedule = scope.ServiceProvider.GetRequiredService<IAnimeScheduleService>();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IEpisodeNotificationDispatcher>();

                await schedule.RefreshAsync(stoppingToken);

                // Iterate every airing in the lookback window. The
                // notifications table's UNIQUE INDEX dedups
                // per-user-per-episode, so re-running the dispatch
                // loop for the same airing on every Worker ping is
                // safe and cheap — each user's CreateAsync is a single
                // INSERT OR IGNORE that no-ops after the first.
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var windowStart = nowUnix - (long)DispatchLookback.TotalSeconds;
                var pending = schedule.GetSchedule()
                    .Where(ep => ep.AiringAt >= windowStart && ep.AiringAt <= nowUnix)
                    .ToList();

                var created = 0;
                foreach (var ep in pending)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    try
                    {
                        var result = await dispatcher.DispatchEpisodeAsync(ep, stoppingToken);
                        created += result.NotificationsCreated;
                        if (result.NotificationsCreated > 0)
                        {
                            _logger.LogInformation(
                                "dispatched {Anime} ep{Ep}: users={Users} created={Created} suppressed={Suppressed}",
                                ep.Title, ep.Episode,
                                result.UsersChecked, result.NotificationsCreated, result.NotificationsSuppressed);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "dispatch failed for {Anime} ep{Ep}", ep.Title, ep.Episode);
                    }
                }

                _logger.LogInformation(
                    "scheduler refresh: {Total} airings in {Lookback}h lookback, {Created} new notifications",
                    pending.Count, DispatchLookback.TotalHours, created);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RefreshAndDispatchAsync failed");
            }
        }

        private async Task BackstopRefreshWatchingCachesAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _services.CreateScope();
                var watchingCache = scope.ServiceProvider.GetRequiredService<IWatchingCacheStore>();
                var refresher = scope.ServiceProvider.GetRequiredService<IWatchingCacheRefreshService>();

                var stale = await watchingCache.GetStaleUidsAsync(TimeSpan.FromHours(24), StartupRefreshBatch);
                foreach (var uid in stale)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await refresher.RefreshAsync(uid, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackstopRefreshWatchingCachesAsync failed");
            }
        }
    }
}
