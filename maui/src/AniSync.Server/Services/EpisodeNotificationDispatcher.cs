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
    /// Idempotency: <see cref="INotificationStore.CreateAsync"/> dedups on
    /// (uid, season, episode_number) AND any of the per-service prefixed
    /// ids for the same physical anime (anilist:21 / mal:21 / kitsu:11061),
    /// passed via the equivalentAnimeIds argument below. A user who flips
    /// primary provider between two cron runs in the dispatch lookback
    /// window therefore doesn't get the same episode twice under two
    /// different id-spaces — the bare unique index on (uid, anime_id, …)
    /// keys on the literal id string and wouldn't catch that.
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

        private readonly IAnimeMappingService _mappingService;
        private readonly IWatchingCacheStore _watchingCache;
        private readonly IWatchingCacheRefreshService _watchingRefresh;
        private readonly INotificationStore _notifications;
        private readonly IPushNotificationService _push;
        private readonly ILogger<EpisodeNotificationDispatcher> _logger;

        public EpisodeNotificationDispatcher(
            IAnimeMappingService mappingService,
            IWatchingCacheStore watchingCache,
            IWatchingCacheRefreshService watchingRefresh,
            INotificationStore notifications,
            IPushNotificationService push,
            ILogger<EpisodeNotificationDispatcher> logger)
        {
            _mappingService = mappingService;
            _watchingCache = watchingCache;
            _watchingRefresh = watchingRefresh;
            _notifications = notifications;
            _push = push;
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

            // Treated as the same anime by the dedup check inside
            // CreateAsync so a primary-service flip mid-window doesn't
            // produce two bell rows for the same episode.
            var equivalentIds = idsByService.Values
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();

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

                var fresh = await _watchingRefresh.RefreshAsync(c.Uid, ct);
                if (fresh != null)
                {
                    caches[i] = fresh;
                    refreshed++;
                }
                else
                {
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
                    LinkPath = $"/meta/{idInUserSpace}/watch/{episode.Episode}",
                    CreatedAt = createdAt,
                };

                var inserted = await _notifications.CreateAsync(record, equivalentIds);
                if (inserted)
                {
                    created++;
                    // Fire-and-forget Web Push to every browser the
                    // user has subscribed. Awaited so any exception
                    // still surfaces via the per-call log, but the
                    // push service swallows transient failures
                    // internally — a dead push provider can't break
                    // notification creation (the row's already in the
                    // DB; the bell still updates on next page render).
                    // No-op when VAPID keys aren't configured.
                    try { await _push.SendAsync(cache.Uid, record); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Web Push send failed for {Uid}", cache.Uid); }
                }
                else
                {
                    suppressed++;
                }
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
