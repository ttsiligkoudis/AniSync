namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Per-user snapshot of the prefixed media ids the user has flagged as
    /// "Watching" on their primary tracker. The episode-notification
    /// dispatcher reads this so the every-5-min cron tick doesn't hit
    /// AniList/MAL/Kitsu list APIs for every user every time.
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
        /// <paramref name="limit"/> is the per-tick rate-limit governor
        /// against the external list APIs.
        /// </summary>
        Task<List<string>> GetStaleUidsAsync(TimeSpan maxAge, int limit);

        /// <summary>
        /// Every cached row. Used by the dispatcher's matching pass —
        /// walks each airing episode and checks every cached user's
        /// HashSet for the resolved per-service id.
        /// </summary>
        Task<List<WatchingCacheEntry>> GetAllAsync();

        Task MarkErrorAsync(string uid, string error);
    }

    public record WatchingCacheEntry(
        string Uid,
        AnimeService Service,
        HashSet<string> MediaIds,
        long RefreshedAt);
}
