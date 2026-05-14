namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Process-local snapshot over the persisted <c>bad_hashes</c> table.
    /// Reads pull from the in-memory <see cref="HashSet{T}"/>, which is
    /// refreshed from <see cref="IConfigStore.GetActiveBadHashesAsync"/>
    /// at most once per refresh window; writes go through to the store
    /// AND eagerly add to the snapshot so the originating process
    /// applies the ban immediately. Singleton so the snapshot survives
    /// across requests within a process. Sharing this between the
    /// addon fetch path and the mark-unplayable controller path avoids
    /// duplicating the refresh-tick coordination in both places.
    /// </summary>
    public interface IBadHashCache
    {
        /// <summary>
        /// Returns the current bad-hash snapshot, refreshing from the
        /// store opportunistically. Fails open: a store error returns
        /// the previous snapshot (possibly stale, possibly empty) and
        /// logs at warning level — never throws.
        /// </summary>
        Task<HashSet<string>> GetSnapshotAsync();

        /// <summary>
        /// Marks <paramref name="infoHash"/> unplayable and persists
        /// it. Passes silently on null/empty/malformed input. Idempotent.
        /// </summary>
        Task MarkAsync(string infoHash);
    }
}
