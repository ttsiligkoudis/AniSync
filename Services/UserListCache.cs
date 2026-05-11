using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace AnimeList.Services
{
    public class UserListCache : IUserListCache
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<UserListCache> _logger;

        // 10 minutes — long enough that a Home → Library → Home navigation cycle
        // is essentially free, short enough that out-of-band writes (scrobble
        // webhooks from another device, edits the user made through the AniList
        // website directly, etc.) can't sit stale forever. User-initiated saves
        // through this app's UI invalidate explicitly so the TTL only really
        // bounds cross-channel staleness.
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        // The six per-user list types the dashboard + library pages read.
        // Trending/Seasonal/Airing/Search are deliberately excluded — Trending
        // and friends aren't user-scoped (they belong in a different cache if
        // we ever add one), Search is query-shaped and unbounded.
        private static readonly ListType[] CachedListTypes =
        [
            ListType.Current,
            ListType.Completed,
            ListType.Planning,
            ListType.Paused,
            ListType.Dropped,
            ListType.Repeating,
        ];

        public UserListCache(IMemoryCache cache, ILogger<UserListCache> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public async Task<List<Meta>> GetOrFetchAsync(TokenData token, ListType listType,
            bool groupSeasons, Func<Task<List<Meta>>> fetcher, bool bypassCache = false)
        {
            var userKey = GetUserKey(token);
            // Skip the cache for anonymous users (no stable id) and for list types
            // outside the cached set (search results, public catalogs). The caller
            // gets the unwrapped fetcher result, so callers don't have to branch.
            if (userKey == null || Array.IndexOf(CachedListTypes, listType) < 0)
                return await fetcher() ?? [];

            var key = BuildKey(token.anime_service, userKey, listType, groupSeasons);

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

            // 12 removes per user (6 list types × 2 group-seasons states). Cheaper
            // than tracking per-user key sets and avoids any threading subtleties
            // around invalidation racing the next read.
            foreach (var lt in CachedListTypes)
            {
                _cache.Remove(BuildKey(token.anime_service, userKey, lt, true));
                _cache.Remove(BuildKey(token.anime_service, userKey, lt, false));
            }

            _logger.LogDebug("UserListCache invalidated for {Service}:{UserKey}.",
                token.anime_service, userKey);
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

        private static string BuildKey(AnimeService svc, string userKey, ListType lt, bool groupSeasons) =>
            $"userlist:{(int)svc}:{userKey}:{(int)lt}:{(groupSeasons ? 1 : 0)}";
    }
}
