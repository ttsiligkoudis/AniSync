namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Refreshes a single user's "Watching"-status anime snapshot in
    /// <c>user_watching_cache</c> from their primary tracker. Lifted out
    /// of <see cref="EpisodeNotificationDispatcher"/> and
    /// <see cref="EpisodeNotificationScheduler"/> so both paths share
    /// identical fetch / normalise / persist / error-handling logic.
    /// </summary>
    public interface IWatchingCacheRefreshService
    {
        /// <summary>
        /// Pulls the user's full list from their primary tracker, filters
        /// to <c>Watching</c> status, and upserts the cache row. Returns
        /// the freshly written entry on success (so callers can update an
        /// in-memory copy without a second read), or null on failure (in
        /// which case the error has already been logged and persisted via
        /// <see cref="IWatchingCacheStore.MarkErrorAsync"/>).
        /// </summary>
        Task<WatchingCacheEntry> RefreshAsync(string uid, CancellationToken ct = default);
    }
}
