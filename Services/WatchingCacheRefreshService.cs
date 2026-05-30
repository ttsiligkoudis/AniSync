using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Pulls a user's primary-tracker list, filters to "Watching", and
    /// upserts <c>user_watching_cache</c>. Both the per-episode dispatcher
    /// and the scheduler's daily backstop go through this single path so
    /// the per-service switch / status filter / error handling lives in
    /// exactly one place.
    /// </summary>
    public class WatchingCacheRefreshService : IWatchingCacheRefreshService
    {
        private readonly IConfigStore _configStore;
        private readonly IAnilistService _anilist;
        private readonly IMalService _mal;
        private readonly IKitsuService _kitsu;
        private readonly IWatchingCacheStore _watchingCache;
        private readonly ILogger<WatchingCacheRefreshService> _logger;

        public WatchingCacheRefreshService(
            IConfigStore configStore,
            IAnilistService anilist,
            IMalService mal,
            IKitsuService kitsu,
            IWatchingCacheStore watchingCache,
            ILogger<WatchingCacheRefreshService> logger)
        {
            _configStore = configStore;
            _anilist = anilist;
            _mal = mal;
            _kitsu = kitsu;
            _watchingCache = watchingCache;
            _logger = logger;
        }

        public async Task<WatchingCacheEntry> RefreshAsync(string uid, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            try
            {
                var token = await _configStore.GetAsync(uid);
                if (token == null || token.anonymousUser)
                {
                    // Anonymous / detached row — store an empty snapshot so the
                    // staleness gate doesn't keep returning this uid every tick.
                    var service = token?.anime_service ?? AnimeService.Anilist;
                    await _watchingCache.UpsertAsync(uid, [], service);
                    return new WatchingCacheEntry(uid, service, [], DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }

                var entries = token.anime_service switch
                {
                    AnimeService.Anilist => await _anilist.GetUserListEntriesAsync(token),
                    AnimeService.MyAnimeList => await _mal.GetUserListEntriesAsync(token),
                    // Trakt's airing-notification snapshot is wired up with the rest of the
                    // Trakt list integration in a later phase — empty for now so a Trakt
                    // primary doesn't fire a Kitsu call with a Trakt token.
                    AnimeService.Trakt => [],
                    _ => await _kitsu.GetUserListEntriesAsync(token),
                };

                var watchingIds = (entries ?? [])
                    .Where(e => NormalizeListStatus(e.Status) == "watching")
                    .Select(e => e.MediaId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToHashSet();

                await _watchingCache.UpsertAsync(uid, watchingIds, token.anime_service);
                return new WatchingCacheEntry(uid, token.anime_service, watchingIds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "watching-cache refresh failed for {Uid}", uid);
                try { await _watchingCache.MarkErrorAsync(uid, ex.Message); } catch { /* best-effort */ }
                return null;
            }
        }
    }
}
