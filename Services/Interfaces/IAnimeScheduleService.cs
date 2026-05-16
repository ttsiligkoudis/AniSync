using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// In-memory cache of today's AniList airing schedule. Owned by the
    /// notification subsystem: the <c>EpisodeNotificationScheduler</c>
    /// hosted service refreshes it once at startup and again at every
    /// UTC midnight, then schedules per-episode <c>Task.Delay</c> timers
    /// off the contents. <c>/api/v1/notifications/count</c> reads
    /// <see cref="GetNextAiringAt"/> so the browser bell can
    /// <c>setTimeout</c> to the precise wake time.
    /// </summary>
    public interface IAnimeScheduleService
    {
        /// <summary>
        /// Pulls a fresh airing window from AniList and replaces the
        /// in-memory snapshot. Idempotent — safe to call concurrently from
        /// the scheduler and ad-hoc paths; the swap is atomic.
        /// </summary>
        Task RefreshAsync(CancellationToken ct = default);

        /// <summary>
        /// Current snapshot of upcoming episodes. May be empty if the first
        /// refresh hasn't completed yet (callers should treat null/empty as
        /// "nothing to schedule").
        /// </summary>
        IReadOnlyList<UpcomingEpisode> GetSchedule();

        /// <summary>
        /// Unix-seconds timestamp of the soonest future airing in the
        /// snapshot, or null when nothing future remains. Used by the bell
        /// to set its next refresh — same value for every signed-in user,
        /// so the lookup is O(1) on a cached field rather than a per-user
        /// scan.
        /// </summary>
        long? GetNextAiringAt();
    }
}
