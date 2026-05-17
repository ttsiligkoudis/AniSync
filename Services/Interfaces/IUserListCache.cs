using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Per-user cache for the six list-type fetches (Current / Completed / Planning /
    /// Paused / Dropped / Repeating) that back the Stremio catalog endpoint when group
    /// anime seasons is enabled. With grouping off the catalog is paginated via the
    /// `skip` extra and the cache is bypassed, since each page is a single upstream
    /// round-trip already. 10-minute TTL with manual invalidation on every save /
    /// delete / bulk-save and every linked-secondary fan-out write — so the user
    /// always sees the result of their own edits, but cross-device or scrobble-driven
    /// changes bound at the TTL. Search results are not cached (query-shaped) and
    /// anonymous users skip caching entirely (no stable identity to key on).
    /// </summary>
    public interface IUserListCache
    {
        /// <summary>
        /// Returns the cached list for this user/service/listType when grouping is
        /// enabled and the entry is present; otherwise invokes <paramref name="fetcher"/>,
        /// caches the result (only when groupSeasons=true), and returns it.
        /// Non-cacheable inputs (anonymous users, search list type, groupSeasons=false)
        /// always fall through to the fetcher.
        /// </summary>
        Task<List<Meta>> GetOrFetchAsync(TokenData token, ListType listType,
            bool groupSeasons, Func<Task<List<Meta>>> fetcher, bool bypassCache = false);

        /// <summary>
        /// Removes every cached entry belonging to this user's identity across the
        /// six cached list types. Called after writes so the next read serves fresh
        /// upstream data instead of stale list state.
        /// </summary>
        void Invalidate(TokenData token);
    }
}
