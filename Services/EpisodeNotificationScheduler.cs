using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Owns the timer schedule for per-user episode notifications. On
    /// startup (and again at every UTC midnight) it refreshes
    /// <see cref="IAnimeScheduleService"/>'s 48h snapshot, then arms one
    /// <c>Task.Delay</c> per future episode. Each delay wakes at the
    /// episode's airing time and hands off to
    /// <see cref="IEpisodeNotificationDispatcher"/> for the per-user
    /// match + insert.
    ///
    /// Replaces the previous Cloudflare Worker cron approach: the worker
    /// only existed because we wanted a timer; .NET has one in-process.
    /// One AniList airing-schedule fetch per day instead of 288.
    /// </summary>
    public class EpisodeNotificationScheduler : BackgroundService
    {
        // Cron grace: a small backstop in case the schedule's airingAt and
        // AniList's "actually publishable" moment disagree by a minute.
        // Dispatcher idempotency absorbs any redundant fires.
        private static readonly TimeSpan AiringFireDelay = TimeSpan.FromSeconds(30);

        // Bounded refresh batch on startup so we don't fan out to every
        // user's tracker at the same moment after a redeploy.
        private const int StartupRefreshBatch = 50;

        private readonly IServiceProvider _services;
        private readonly ILogger<EpisodeNotificationScheduler> _logger;

        // Keys ("anilistId:episode") we've already armed timers for. Survives
        // the daily refresh so reconciliation doesn't double-schedule.
        private readonly HashSet<string> _armed = [];
        private readonly object _armedLock = new();

        public EpisodeNotificationScheduler(IServiceProvider services, ILogger<EpisodeNotificationScheduler> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Initial refresh — best-effort. Failures are logged inside
            // RefreshAsync; we keep looping so the next midnight tick retries.
            await RefreshAndArmAsync(stoppingToken);

            // Up-front, also refresh stale watching caches so the first
            // episode dispatch isn't slowed by a large inline refresh batch.
            await BackstopRefreshWatchingCachesAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                // Sleep until the next UTC midnight, then re-arm. Daily refresh
                // is the cadence at which AniList's published schedule moves.
                var now = DateTimeOffset.UtcNow;
                var nextMidnight = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
                var delay = nextMidnight - now;
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }

                await RefreshAndArmAsync(stoppingToken);
                await BackstopRefreshWatchingCachesAsync(stoppingToken);
            }
        }

        private async Task RefreshAndArmAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _services.CreateScope();
                var schedule = scope.ServiceProvider.GetRequiredService<IAnimeScheduleService>();
                await schedule.RefreshAsync(stoppingToken);

                var entries = schedule.GetSchedule();
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var armedThisPass = 0;
                var recoveredThisPass = 0;
                foreach (var ep in entries)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var key = $"{ep.AnilistId}:{ep.Episode}";
                    lock (_armedLock)
                    {
                        if (!_armed.Add(key)) continue;
                    }

                    if (ep.AiringAt <= nowUnix)
                    {
                        // Past episode — dispatch immediately rather than
                        // arm-and-fire-later. Covers the Fly.io auto-stop
                        // sleep-recovery case: when the machine was asleep
                        // at the original airing moment, the Task.Delay
                        // timer died with the process and the user never
                        // got a notification. On wake the schedule fetch
                        // (24h lookback) surfaces those past episodes and
                        // we dispatch them here. NotificationStore's UNIQUE
                        // INDEX (uid, anime_id, season, episode) makes the
                        // dispatch idempotent — episodes that DID fire
                        // before the sleep (or already fired in a prior
                        // RefreshAndArmAsync pass) collide and become
                        // no-ops via INSERT OR IGNORE.
                        recoveredThisPass++;
                        _ = DispatchImmediateAsync(ep, key, stoppingToken);
                    }
                    else
                    {
                        armedThisPass++;
                        _ = ArmTimerAsync(ep, key, stoppingToken);
                    }
                }

                _logger.LogInformation(
                    "EpisodeNotificationScheduler armed {Armed} future timers, recovered {Recovered} past episodes (snapshot has {Total} entries)",
                    armedThisPass, recoveredThisPass, entries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EpisodeNotificationScheduler.RefreshAndArmAsync failed");
            }
        }

        /// <summary>
        /// Fires a past episode through the dispatcher immediately —
        /// covers sleep-recovery on auto-stopped hosts where the
        /// original Task.Delay timer didn't survive the process death.
        /// Same scope / logging / cleanup shape as <see cref="ArmTimerAsync"/>;
        /// just skips the wait.
        /// </summary>
        private async Task DispatchImmediateAsync(UpcomingEpisode ep, string key, CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _services.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IEpisodeNotificationDispatcher>();
                var result = await dispatcher.DispatchEpisodeAsync(ep, stoppingToken);
                _logger.LogInformation(
                    "sleep-recovery dispatch {Anime} ep{Ep} (aired {Ago}s ago): users={Users} refreshed={Refreshed} failed={Failed} created={Created} suppressed={Suppressed}",
                    ep.Title, ep.Episode,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ep.AiringAt,
                    result.UsersChecked, result.CachesRefreshed,
                    result.CachesFailed, result.NotificationsCreated, result.NotificationsSuppressed);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — drop the dispatch silently.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DispatchImmediateAsync failed for {Key}", key);
            }
            finally
            {
                // Same dedup-slot cleanup as ArmTimerAsync — clear the
                // key so a later refresh that still has this episode
                // in its window can re-attempt (no-ops via INSERT OR
                // IGNORE on the second pass).
                lock (_armedLock) { _armed.Remove(key); }
            }
        }

        private async Task ArmTimerAsync(UpcomingEpisode ep, string key, CancellationToken stoppingToken)
        {
            try
            {
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var delaySec = Math.Max(0, ep.AiringAt - nowUnix);
                var delay = TimeSpan.FromSeconds(delaySec).Add(AiringFireDelay);
                await Task.Delay(delay, stoppingToken);

                if (stoppingToken.IsCancellationRequested) return;

                using var scope = _services.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IEpisodeNotificationDispatcher>();
                var result = await dispatcher.DispatchEpisodeAsync(ep, stoppingToken);
                _logger.LogInformation(
                    "episode dispatch {Anime} ep{Ep}: users={Users} refreshed={Refreshed} failed={Failed} created={Created} suppressed={Suppressed}",
                    ep.Title, ep.Episode, result.UsersChecked, result.CachesRefreshed,
                    result.CachesFailed, result.NotificationsCreated, result.NotificationsSuppressed);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — drop the timer silently.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArmTimerAsync failed for {Key}", key);
            }
            finally
            {
                // Free the dedup slot so a midnight re-arm of the same key
                // (rare — would mean a schedule that didn't shift) re-fires.
                // In practice each (anilistId, episode) only airs once.
                lock (_armedLock) { _armed.Remove(key); }
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
