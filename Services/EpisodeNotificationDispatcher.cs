using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Per-episode dispatch: given an airing entry that just fired, find
    /// every cached user watching that anime and insert a notification.
    /// Stale watching caches (explicitly invalidated via UI edit hooks or
    /// older than the staleness backstop) get refreshed first so a user
    /// who added the show today doesn't miss tonight's episode.
    ///
    /// Idempotency: <see cref="INotificationStore.CreateAsync"/> uses
    /// <c>INSERT OR IGNORE</c> on the (uid, anime_id, season, episode_number)
    /// unique index, so a re-fired timer (after a crash + restart, or a
    /// double-scheduled episode) produces no duplicates.
    /// </summary>
    public class EpisodeNotificationDispatcher : IEpisodeNotificationDispatcher
    {
        // Backstop staleness: caches older than this get refreshed when an
        // episode airs for one of the user's matching shows. UI edits already
        // mark stale immediately (via UserListCache.Invalidate), so this
        // only catches external edits made through AniList/MAL/Kitsu's own
        // websites and inactive sessions.
        private static readonly TimeSpan StaleBackstop = TimeSpan.FromHours(24);

        // Per-episode safety cap on inline refreshes. Keeps a Saturday-evening
        // burst from fan-out-fetching every user's list against AniList in
        // the same second. Users beyond this cap are skipped on this episode
        // but picked up by the scheduler's daily backstop refresh.
        private const int MaxInlineRefreshes = 25;

        private readonly IConfigStore _configStore;
        private readonly IAnimeMappingService _mappingService;
        private readonly IAnilistService _anilistService;
        private readonly IMalService _malService;
        private readonly IKitsuService _kitsuService;
        private readonly IWatchingCacheStore _watchingCache;
        private readonly INotificationStore _notifications;
        private readonly ILogger<EpisodeNotificationDispatcher> _logger;

        public EpisodeNotificationDispatcher(
            IConfigStore configStore,
            IAnimeMappingService mappingService,
            IAnilistService anilistService,
            IMalService malService,
            IKitsuService kitsuService,
            IWatchingCacheStore watchingCache,
            INotificationStore notifications,
            ILogger<EpisodeNotificationDispatcher> logger)
        {
            _configStore = configStore;
            _mappingService = mappingService;
            _anilistService = anilistService;
            _malService = malService;
            _kitsuService = kitsuService;
            _watchingCache = watchingCache;
            _notifications = notifications;
            _logger = logger;
        }

        public async Task<DispatchResult> DispatchEpisodeAsync(UpcomingEpisode episode, CancellationToken ct = default)
        {
            // Resolve the AniList id into each per-service id ONCE up front.
            // Mapping miss → MAL/Kitsu users for this show are silently
            // skipped (same shape as AnilistFallback.ResolveExternalIdAsync).
            await _mappingService.EnsureLoadedAsync();
            var anilistPrefixed = $"{anilistPrefix}{episode.AnilistId}";
            AnimeIdMapping mapping = null;
            try { mapping = await _mappingService.GetAnilistMapping(anilistPrefixed); }
            catch (Exception ex) { _logger.LogDebug(ex, "GetAnilistMapping failed for {Id}", anilistPrefixed); }

            var idsByService = new Dictionary<AnimeService, string>
            {
                [AnimeService.Anilist] = anilistPrefixed,
                [AnimeService.MyAnimeList] = mapping?.MalId != null ? $"{malPrefix}{mapping.MalId}" : null,
                [AnimeService.Kitsu] = mapping?.KitsuId != null ? $"{kitsuPrefix}{mapping.KitsuId}" : null,
            };

            var caches = await _watchingCache.GetAllAsync();
            if (caches.Count == 0)
            {
                return new DispatchResult(0, 0, 0, 0, 0);
            }

            // Refresh stale caches inline so users who edited today don't miss
            // tonight's notifications. Bounded to MaxInlineRefreshes so a
            // simultaneous-airing burst doesn't melt the upstream APIs.
            var refreshed = 0;
            var refreshFailed = 0;
            var staleCutoff = DateTimeOffset.UtcNow.Subtract(StaleBackstop).ToUnixTimeSeconds();
            for (var i = 0; i < caches.Count && refreshed + refreshFailed < MaxInlineRefreshes; i++)
            {
                if (ct.IsCancellationRequested) break;
                var c = caches[i];
                if (c.RefreshedAt >= staleCutoff && c.RefreshedAt > 0) continue;

                try
                {
                    var token = await _configStore.GetAsync(c.Uid);
                    if (token == null || token.anonymousUser)
                    {
                        await _watchingCache.UpsertAsync(c.Uid, [], token?.anime_service ?? c.Service);
                        refreshed++;
                        caches[i] = new WatchingCacheEntry(c.Uid, c.Service, [], DateTimeOffset.UtcNow.ToUnixTimeSeconds());
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

                    await _watchingCache.UpsertAsync(c.Uid, watchingIds, token.anime_service);
                    refreshed++;
                    caches[i] = new WatchingCacheEntry(c.Uid, token.anime_service, watchingIds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "inline watching-cache refresh failed for {Uid}", c.Uid);
                    try { await _watchingCache.MarkErrorAsync(c.Uid, ex.Message); } catch { /* best-effort */ }
                    refreshFailed++;
                }
            }

            // Match + insert.
            var created = 0;
            var suppressed = 0;
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var cache in caches)
            {
                if (ct.IsCancellationRequested) break;
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
                    AnimeTitle = episode.Title,
                    EpisodeNumber = episode.Episode,
                    Season = null,
                    ThumbnailUrl = episode.CoverImage,
                    LinkPath = $"/anime/{idInUserSpace}/watch/{episode.Episode}",
                    CreatedAt = createdAt,
                };

                var inserted = await _notifications.CreateAsync(record);
                if (inserted) created++;
                else suppressed++;
            }

            return new DispatchResult(
                UsersChecked: caches.Count,
                CachesRefreshed: refreshed,
                CachesFailed: refreshFailed,
                NotificationsCreated: created,
                NotificationsSuppressed: suppressed);
        }
    }
}
