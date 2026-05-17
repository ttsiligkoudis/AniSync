using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Per-airing dispatch state, sitting alongside the in-memory
    /// <see cref="IAnimeScheduleService"/> snapshot. Tracks which
    /// AniList airings the scheduler has already run the dispatch loop
    /// for so repeated wake pings (the Cloudflare Worker fires once
    /// per minute, AniSync's own startup re-arms etc.) collapse to
    /// cheap no-ops without re-running the per-user fan-out.
    /// </summary>
    public interface IScheduleStore
    {
        /// <summary>
        /// Upserts a batch of airing entries from the upstream schedule.
        /// On conflict (matching anilist_id + episode) the entry's
        /// title / cover_image / airing_at / refreshed_at are updated
        /// but <c>notified_at</c> is preserved — so an entry the
        /// scheduler already dispatched stays marked notified across
        /// refreshes.
        /// </summary>
        Task UpsertManyAsync(IReadOnlyList<UpcomingEpisode> entries);

        /// <summary>
        /// Returns every entry that hasn't been dispatched yet
        /// (<c>notified_at IS NULL</c>). The scheduler hands these to
        /// the dispatcher on every wake — past entries fire
        /// immediately, future entries are armed with a
        /// <c>Task.Delay</c>.
        /// </summary>
        Task<List<UpcomingEpisode>> GetPendingAsync();

        /// <summary>
        /// Marks a single entry's dispatch loop as completed. Idempotent
        /// — calling twice with the same key just refreshes the
        /// <c>notified_at</c> timestamp.
        /// </summary>
        Task MarkNotifiedAsync(int anilistId, int episode);

        /// <summary>
        /// Drops entries older than <paramref name="cutoffUnix"/>. Called
        /// at daily refresh to keep the table compact; the typical
        /// long-tail is just-aired entries the scheduler dispatched
        /// hours ago, which there's no reason to keep around.
        /// </summary>
        Task<int> PruneOlderThanAsync(long cutoffUnix);
    }
}
