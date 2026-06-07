using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// NOTE: no longer caches lists (see <see cref="IUserListCache"/>). The in-memory list cache + its
    /// GetOrFetchAsync read path were removed; all that's left is the edit hook below. Kept under the old
    /// name/interface so the existing <see cref="Invalidate"/> call sites (UserApiController /
    /// MetaController / SyncService) don't churn.
    /// </summary>
    public class UserListCache : IUserListCache
    {
        private readonly IWatchingCacheStore _watchingCache;
        private readonly IConfigStore _configStore;
        private readonly ILogger<UserListCache> _logger;

        public UserListCache(
            IWatchingCacheStore watchingCache,
            IConfigStore configStore,
            ILogger<UserListCache> logger)
        {
            _watchingCache = watchingCache;
            _configStore = configStore;
            _logger = logger;
        }

        // Called on every in-app list edit (save / delete / bulk-save / linked-secondary fan-out). Marks
        // this user's persistent watching cache stale so the next episode-dispatcher pass re-fetches their
        // "Watching" set instead of using the pre-edit snapshot. Fire-and-forget + best-effort: a missed
        // mark is self-correcting via the daily backstop refresh.
        public void Invalidate(TokenData token)
        {
            if (token == null || token.anonymousUser) return;
            _ = MarkWatchingStaleAsync(token);
        }

        private async Task MarkWatchingStaleAsync(TokenData token)
        {
            try
            {
                var (uid, _) = await _configStore.FindUidByIdentityAsync(token);
                if (!string.IsNullOrEmpty(uid))
                    await _watchingCache.MarkStaleAsync(uid);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WatchingCacheStore.MarkStaleAsync failed (best-effort)");
            }
        }
    }
}
