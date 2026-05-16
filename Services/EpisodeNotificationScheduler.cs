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
                foreach (var ep in entries)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    // Skip episodes already past — startup or daily refresh can
                    // surface these inside the lookback window. The dispatcher's
                    // idempotency would absorb a re-fire anyway, but skipping is
                    // cheaper than firing-then-no-op'ing.
                    if (ep.AiringAt <= nowUnix) continue;

                    var key = $"{ep.AnilistId}:{ep.Episode}";
                    lock (_armedLock)
                    {
                        if (!_armed.Add(key)) continue;
                    }

                    armedThisPass++;
                    _ = ArmTimerAsync(ep, key, stoppingToken);
                }

                _logger.LogInformation(
                    "EpisodeNotificationScheduler armed {Armed} timers (snapshot has {Total} entries)",
                    armedThisPass, entries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EpisodeNotificationScheduler.RefreshAndArmAsync failed");
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
                var configStore = scope.ServiceProvider.GetRequiredService<IConfigStore>();
                var anilist = scope.ServiceProvider.GetRequiredService<IAnilistService>();
                var mal = scope.ServiceProvider.GetRequiredService<IMalService>();
                var kitsu = scope.ServiceProvider.GetRequiredService<IKitsuService>();

                var stale = await watchingCache.GetStaleUidsAsync(TimeSpan.FromHours(24), StartupRefreshBatch);
                foreach (var uid in stale)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    try
                    {
                        var token = await configStore.GetAsync(uid);
                        if (token == null || token.anonymousUser)
                        {
                            await watchingCache.UpsertAsync(uid, [], token?.anime_service ?? AnimeService.Anilist);
                            continue;
                        }

                        var entries = token.anime_service switch
                        {
                            AnimeService.Anilist => await anilist.GetUserListEntriesAsync(token),
                            AnimeService.MyAnimeList => await mal.GetUserListEntriesAsync(token),
                            _ => await kitsu.GetUserListEntriesAsync(token),
                        };

                        var watchingIds = (entries ?? [])
                            .Where(e => NormalizeListStatus(e.Status) == "watching")
                            .Select(e => e.MediaId)
                            .Where(id => !string.IsNullOrEmpty(id))
                            .ToHashSet();

                        await watchingCache.UpsertAsync(uid, watchingIds, token.anime_service);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "backstop watching-cache refresh failed for {Uid}", uid);
                        try { await watchingCache.MarkErrorAsync(uid, ex.Message); } catch { /* best-effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackstopRefreshWatchingCachesAsync failed");
            }
        }
    }
}
