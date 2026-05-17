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
    public class EpisodeNotificationScheduler : BackgroundService, IEpisodeNotificationScheduler
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

        /// <summary>
        /// External-trigger entry point. Runs the same refresh + arm +
        /// dispatch-past-episodes pass the background loop runs at
        /// midnight, on demand. Used by the <c>POST /api/v1/cron/check-releases</c>
        /// endpoint that the Cloudflare Worker pings when an episode is
        /// due — wakes the Fly.io machine, runs this, and the dispatcher's
        /// 24h recovery logic catches anything that just aired.
        /// </summary>
        public Task TriggerRefreshAsync(CancellationToken ct = default)
            => RefreshAndArmAsync(ct);

        private async Task RefreshAndArmAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _services.CreateScope();
                var schedule = scope.ServiceProvider.GetRequiredService<IAnimeScheduleService>();
                var scheduleStore = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
                await schedule.RefreshAsync(stoppingToken);

                // Pull "things still needing dispatch" from the persistent
                // store instead of the in-memory snapshot. The store
                // preserves notified_at across refreshes, so an entry the
                // scheduler already dispatched in a previous pass is
                // filtered out here without having to consult the in-
                // memory _armed set. Covers the wake-from-sleep recovery
                // case (Fly.io auto-stop killed the Task.Delay) and
                // collapses redundant Cloudflare Worker pings to cheap
                // no-ops (the per-airing partial index keeps the lookup
                // O(log n) regardless of how many historical entries
                // accumulate before the daily prune runs).
                var pending = await scheduleStore.GetPendingAsync();
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var armedThisPass = 0;
                var recoveredThisPass = 0;
                foreach (var ep in pending)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var key = $"{ep.AnilistId}:{ep.Episode}";
                    lock (_armedLock)
                    {
                        // In-memory dedup for the brief window between
                        // "this RefreshAndArmAsync saw the row pending"
                        // and "DispatchImmediate / ArmTimer has marked it
                        // notified." Without this, two concurrent
                        // RefreshAndArmAsync calls (e.g. the BG service's
                        // midnight tick overlapping with a Worker-driven
                        // trigger) could both see the same pending row
                        // and double-dispatch before either's MarkNotified
                        // ran.
                        if (!_armed.Add(key)) continue;
                    }

                    if (ep.AiringAt <= nowUnix)
                    {
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
                    "EpisodeNotificationScheduler: {Pending} pending → {Armed} future timers + {Recovered} immediate dispatches",
                    pending.Count, armedThisPass, recoveredThisPass);
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
            var success = false;
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

                // Mark the schedule entry notified so subsequent
                // RefreshAndArmAsync passes don't re-dispatch (the per-
                // user UNIQUE INDEX would still no-op duplicates, but
                // skipping the work entirely is cheaper).
                var scheduleStore = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
                await scheduleStore.MarkNotifiedAsync(ep.AnilistId, ep.Episode);
                success = true;
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
                // Clear the in-memory dedup slot. On failure the entry
                // stays pending in the store (notified_at NULL) so a
                // future RefreshAndArmAsync retries it.
                lock (_armedLock) { _armed.Remove(key); }
                if (!success)
                {
                    _logger.LogDebug("DispatchImmediateAsync: entry {Key} left pending for next refresh", key);
                }
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

                // Mark notified in the schedule store so the next
                // RefreshAndArmAsync skips this entry. On failure the
                // entry stays pending and gets retried via the recovery
                // path on the next wake / schedule pass.
                var scheduleStore = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
                await scheduleStore.MarkNotifiedAsync(ep.AnilistId, ep.Episode);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — drop the timer silently. Entry stays
                // pending in the store so the next process picks it up.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArmTimerAsync failed for {Key}", key);
            }
            finally
            {
                // Free the in-memory dedup slot. The persistent
                // notified_at flag is the durable record; this is just
                // for inter-task coordination within a single process.
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
