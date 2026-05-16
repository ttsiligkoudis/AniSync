namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Per-user snapshot of the prefixed media ids the user has flagged as
    /// "Watching" on their primary tracker. Read by the per-episode
    /// notification scheduler to decide which users to notify when an
    /// episode airs; written by login/startup/scheduler refresh paths.
    /// </summary>
    public interface IWatchingCacheStore
    {
        /// <summary>
        /// Replaces the cached list for a uid in a single statement
        /// (INSERT … ON CONFLICT DO UPDATE). Clears last_error / last_error_at
        /// on success so retried-after-failure flows recover.
        /// </summary>
        Task UpsertAsync(string uid, IReadOnlyCollection<string> mediaIds, AnimeService service);

        Task<WatchingCacheEntry> GetAsync(string uid);

        /// <summary>
        /// Returns uids whose cache is older than <paramref name="maxAge"/>
        /// OR who have no cache row yet (LEFT JOIN against configs).
        /// <paramref name="limit"/> caps the per-call refresh batch so a
        /// startup or daily refresh doesn't fan out into hundreds of
        /// upstream calls at once.
        /// </summary>
        Task<List<string>> GetStaleUidsAsync(TimeSpan maxAge, int limit);

        /// <summary>
        /// Every cached row. Used by the per-episode dispatcher's match
        /// pass — walks each airing episode and checks every user's
        /// HashSet for the resolved per-service id.
        /// </summary>
        Task<List<WatchingCacheEntry>> GetAllAsync();

        Task MarkErrorAsync(string uid, string error);

        /// <summary>
        /// Forces the next read to treat this row as stale by zeroing its
        /// <c>refreshed_at</c>. Called from the UI-edit / scrobble paths
        /// alongside the existing <see cref="IUserListCache.Invalidate"/>
        /// hook so a save the user just made shows up on the next episode
        /// dispatch instead of waiting for the daily backstop.
        /// </summary>
        Task MarkStaleAsync(string uid);
    }

    public record WatchingCacheEntry(
        string Uid,
        AnimeService Service,
        HashSet<string> MediaIds,
        long RefreshedAt);
}
