using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Per-user cache for the six list-type fetches (Current / Completed / Planning /
    /// Paused / Dropped / Repeating) that back the dashboard + library web-app pages.
    /// 10-minute TTL with manual invalidation on every save / delete / bulk-save and
    /// every linked-secondary fan-out write — so the user always sees the result of
    /// their own edits, but cross-device or scrobble-driven changes bound at the TTL.
    /// Search results are not cached (query-shaped) and anonymous users skip caching
    /// entirely (no stable identity to key on).
    /// </summary>
    public interface IUserListCache
    {
        /// <summary>
        /// Returns the cached list for this user/service/listType when present and not
        /// bypassed; otherwise invokes <paramref name="fetcher"/>, caches the result,
        /// and returns it. Non-cacheable inputs (anonymous users, search list type)
        /// always fall through to the fetcher.
        /// </summary>
        Task<List<Meta>> GetOrFetchAsync(TokenData token, ListType listType,
            bool groupSeasons, Func<Task<List<Meta>>> fetcher, bool bypassCache = false);

        /// <summary>
        /// Removes every cached entry belonging to this user's identity across all
        /// six list types and both group-seasons states. Called after writes so the
        /// next read serves fresh upstream data instead of stale list state.
        /// </summary>
        void Invalidate(TokenData token);
    }
}
