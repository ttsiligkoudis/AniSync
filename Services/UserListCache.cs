using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace AnimeList.Services
{
    public class UserListCache : IUserListCache
    {
        private readonly IMemoryCache _cache;
        private readonly IWatchingCacheStore _watchingCache;
        private readonly IConfigStore _configStore;
        private readonly ILogger<UserListCache> _logger;

        // 10 minutes — long enough that a Home → Library → Home navigation cycle
        // is essentially free, short enough that out-of-band writes (scrobble
        // webhooks from another device, edits the user made through the AniList
        // website directly, etc.) can't sit stale forever. User-initiated saves
        // through this app's UI invalidate explicitly so the TTL only really
        // bounds cross-channel staleness.
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        // The six per-user list types the Stremio catalog endpoint reads when
        // group-anime-seasons is enabled. Trending / Seasonal / Airing / Search
        // are deliberately excluded — Trending and friends aren't user-scoped,
        // Search is query-shaped and unbounded.
        private static readonly ListType[] CachedListTypes =
        [
            ListType.Current,
            ListType.Completed,
            ListType.Planning,
            ListType.Paused,
            ListType.Dropped,
            ListType.Repeating,
        ];

        public UserListCache(
            IMemoryCache cache,
            IWatchingCacheStore watchingCache,
            IConfigStore configStore,
            ILogger<UserListCache> logger)
        {
            _cache = cache;
            _watchingCache = watchingCache;
            _configStore = configStore;
            _logger = logger;
        }

        public async Task<List<Meta>> GetOrFetchAsync(TokenData token, ListType listType,
            bool groupSeasons, Func<Task<List<Meta>>> fetcher, bool bypassCache = false)
        {
            var userKey = GetUserKey(token);
            // Cache only applies in the Stremio catalog flow with grouping enabled —
            // that's the mode where one upstream round-trip returns the entire
            // deduped library and a hot cache amortises it across paginated UI
            // renders. With grouping off, Stremio pages user-list catalogs via the
            // `skip` extra (one upstream page per request), so a per-page cache
            // wouldn't earn its memory. Anonymous users and non-user list types
            // (search, trending, …) also skip the cache.
            if (userKey == null
                || !groupSeasons
                || Array.IndexOf(CachedListTypes, listType) < 0)
                return await fetcher() ?? [];

            var key = BuildKey(token.anime_service, userKey, listType);

            if (bypassCache)
            {
                _cache.Remove(key);
            }
            else if (_cache.TryGetValue<List<Meta>>(key, out var cached) && cached != null)
            {
                return cached;
            }

            var fresh = await fetcher() ?? [];
            _cache.Set(key, fresh, Ttl);
            return fresh;
        }

        public void Invalidate(TokenData token)
        {
            var userKey = GetUserKey(token);
            if (userKey == null) return;

            // 6 removes per user — one entry per cached list type. groupSeasons=false
            // never lands in the cache so there's nothing to clear for that state.
            foreach (var lt in CachedListTypes)
            {
                _cache.Remove(BuildKey(token.anime_service, userKey, lt));
            }

            // Fire-and-forget the persistent watching-cache invalidation so the
            // next episode dispatcher pass re-fetches this user's list instead
            // of using the snapshot from before this edit. Best-effort: a missed
            // mark is self-correcting (the daily backstop refresh catches up).
            _ = MarkWatchingStaleAsync(token);

            _logger.LogDebug("UserListCache invalidated for {Service}:{UserKey}.",
                token.anime_service, userKey);
        }

        private async Task MarkWatchingStaleAsync(TokenData token)
        {
            try
            {
                if (token == null || token.anonymousUser) return;
                var (uid, _) = await _configStore.FindUidByIdentityAsync(token);
                if (!string.IsNullOrEmpty(uid))
                {
                    await _watchingCache.MarkStaleAsync(uid);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WatchingCacheStore.MarkStaleAsync failed (best-effort)");
            }
        }

        // Identity preference order matches what each service exposes after auth:
        // AniList + MAL surface user_id from the OAuth /me lookup, Kitsu carries
        // username (its REST API keys lists off username, not numeric id). Falling
        // back to username covers cases where user_id is null but username is
        // present. Returning null means "don't cache" — anonymous Kitsu has neither.
        private static string GetUserKey(TokenData token)
        {
            if (token == null || token.anonymousUser) return null;
            if (!string.IsNullOrEmpty(token.user_id)) return "id:" + token.user_id;
            if (!string.IsNullOrEmpty(token.username)) return "u:" + token.username;
            return null;
        }

        private static string BuildKey(AnimeService svc, string userKey, ListType lt) =>
            $"userlist:{(int)svc}:{userKey}:{(int)lt}";
    }
}
