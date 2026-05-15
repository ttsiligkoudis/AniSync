using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// One tick of "find newly-aired episodes and create per-user
    /// notifications for users tracking them". Driven by the
    /// <c>cf-episode-notifier</c> Cloudflare Worker every 5 minutes.
    ///
    /// Pipeline:
    ///   1. Refresh stale <see cref="IWatchingCacheStore"/> snapshots — at most
    ///      <see cref="RefreshBatchLimit"/> users per tick so we don't hammer
    ///      AniList/MAL/Kitsu list APIs.
    ///   2. Pull the AniList airing schedule for the [now-1h, now+24h] window.
    ///   3. For every airing entry, translate the AniList id into each user's
    ///      primary-service id (via the existing <see cref="IAnimeMappingService"/>),
    ///      then walk every cached user and create a notification when their
    ///      Watching set contains the show. <see cref="INotificationStore.CreateAsync"/>
    ///      is idempotent on (uid, anime_id, season, episode_number) so the
    ///      sliding window naturally dedupes across overlapping ticks.
    ///
    /// All upstream calls (`airingAt` from AniList, the per-user list fetches)
    /// operate in Unix UTC, so timezones / DST are a non-issue throughout.
    /// </summary>
    public class EpisodeNotificationDispatcher : IEpisodeNotificationDispatcher
    {
        private static readonly TimeSpan RefreshLookback = TimeSpan.FromHours(6);
        private const int RefreshBatchLimit = 50;
        private const int NotifyWindowHours = 24;
        private const int NotifyPastGraceHours = 1;

        private readonly IAnilistFallback _anilistFallback;
        private readonly IConfigStore _configStore;
        private readonly IAnimeMappingService _mappingService;
        private readonly IAnilistService _anilistService;
        private readonly IMalService _malService;
        private readonly IKitsuService _kitsuService;
        private readonly IWatchingCacheStore _watchingCache;
        private readonly INotificationStore _notifications;
        private readonly ILogger<EpisodeNotificationDispatcher> _logger;

        public EpisodeNotificationDispatcher(
            IAnilistFallback anilistFallback,
            IConfigStore configStore,
            IAnimeMappingService mappingService,
            IAnilistService anilistService,
            IMalService malService,
            IKitsuService kitsuService,
            IWatchingCacheStore watchingCache,
            INotificationStore notifications,
            ILogger<EpisodeNotificationDispatcher> logger)
        {
            _anilistFallback = anilistFallback;
            _configStore = configStore;
            _mappingService = mappingService;
            _anilistService = anilistService;
            _malService = malService;
            _kitsuService = kitsuService;
            _watchingCache = watchingCache;
            _notifications = notifications;
            _logger = logger;
        }

        public async Task<DispatchResult> RunAsync(CancellationToken ct = default)
        {
            var cachesRefreshed = 0;
            var cachesFailed = 0;
            var notificationsCreated = 0;
            var notificationsSuppressed = 0;

            // Step 1: refresh stale Watching caches.
            var staleUids = await _watchingCache.GetStaleUidsAsync(RefreshLookback, RefreshBatchLimit);
            foreach (var uid in staleUids)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var token = await _configStore.GetAsync(uid);
                    if (token == null || token.anonymousUser)
                    {
                        // Anonymous / detached row — record success against
                        // an empty set so the staleness gate doesn't keep
                        // returning it every tick.
                        await _watchingCache.UpsertAsync(uid, [], token?.anime_service ?? AnimeService.Anilist);
                        cachesRefreshed++;
                        continue;
                    }

                    var entries = token.anime_service switch
                    {
                        AnimeService.Anilist => await _anilistService.GetUserListEntriesAsync(token),
                        AnimeService.MyAnimeList => await _malService.GetUserListEntriesAsync(token),
                        _ => await _kitsuService.GetUserListEntriesAsync(token),
                    };

                    var watchingIds = (entries ?? [])
                        .Where(e => NormalizeListStatus(e.Status) == "watching")
                        .Select(e => e.MediaId)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToHashSet();

                    await _watchingCache.UpsertAsync(uid, watchingIds, token.anime_service);
                    cachesRefreshed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "watching-cache refresh failed for {Uid}", uid);
                    try { await _watchingCache.MarkErrorAsync(uid, ex.Message); } catch { /* best-effort */ }
                    cachesFailed++;
                }
            }

            // Step 2: pull the airing schedule for the notification window.
            var now = DateTimeOffset.UtcNow;
            var startUnix = now.AddHours(-NotifyPastGraceHours).ToUnixTimeSeconds();
            var endUnix = now.AddHours(NotifyWindowHours).ToUnixTimeSeconds();
            var schedule = await _anilistFallback.GetUpcomingEpisodesAsync(startUnix, endUnix);
            if (schedule.Count == 0)
            {
                return new DispatchResult(cachesRefreshed, cachesFailed, 0, 0, 0);
            }

            // Step 3: match airing entries against cached Watching lists.
            var allCaches = await _watchingCache.GetAllAsync();
            if (allCaches.Count == 0)
            {
                return new DispatchResult(cachesRefreshed, cachesFailed, schedule.Count, 0, 0);
            }

            await _mappingService.EnsureLoadedAsync();
            var createdAt = now.ToUnixTimeSeconds();

            foreach (var sched in schedule)
            {
                if (ct.IsCancellationRequested) break;

                // Resolve the AniList id into each per-service id ONCE per
                // airing entry instead of per user — typically 200-300 cached
                // users vs. ~50 airing entries makes this the right axis to
                // amortise on.
                var anilistPrefixed = $"{anilistPrefix}{sched.AnilistId}";
                AnimeIdMapping mapping = null;
                try { mapping = await _mappingService.GetAnilistMapping(anilistPrefixed); }
                catch { /* mapping miss → MAL/Kitsu users for this show are skipped, same shape as ResolveExternalIdAsync */ }

                var idsByService = new Dictionary<AnimeService, string>
                {
                    [AnimeService.Anilist] = anilistPrefixed,
                    [AnimeService.MyAnimeList] = mapping?.MalId != null ? $"{malPrefix}{mapping.MalId}" : null,
                    [AnimeService.Kitsu] = mapping?.KitsuId != null ? $"{kitsuPrefix}{mapping.KitsuId}" : null,
                };

                foreach (var cache in allCaches)
                {
                    if (!idsByService.TryGetValue(cache.Service, out var idInUserSpace)
                        || string.IsNullOrEmpty(idInUserSpace))
                    {
                        continue;
                    }
                    if (!cache.MediaIds.Contains(idInUserSpace)) continue;

                    var record = new NotificationRecord
                    {
                        Uid = cache.Uid,
                        Service = cache.Service,
                        AnimeId = idInUserSpace,
                        AnimeTitle = sched.Title,
                        EpisodeNumber = sched.Episode,
                        Season = null,
                        ThumbnailUrl = sched.CoverImage,
                        LinkPath = $"/anime/{idInUserSpace}/watch/{sched.Episode}",
                        CreatedAt = createdAt,
                    };

                    var inserted = await _notifications.CreateAsync(record);
                    if (inserted) notificationsCreated++;
                    else notificationsSuppressed++;
                }
            }

            return new DispatchResult(
                CachesRefreshed: cachesRefreshed,
                CachesFailed: cachesFailed,
                AiringChecked: schedule.Count,
                NotificationsCreated: notificationsCreated,
                NotificationsSuppressed: notificationsSuppressed);
        }
    }
}
