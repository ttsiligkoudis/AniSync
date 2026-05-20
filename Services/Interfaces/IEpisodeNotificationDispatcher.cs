using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Per-episode dispatch step: given one airing entry that just fired,
    /// refresh any stale watching caches relevant to this dispatch, match
    /// against every cached user's "Watching" set, and insert
    /// idempotent notification rows for matching users. Called by
    /// <c>EpisodeNotificationScheduler</c> when each <c>Task.Delay</c>
    /// wakes at its airing time.
    /// </summary>
    public interface IEpisodeNotificationDispatcher
    {
        Task<DispatchResult> DispatchEpisodeAsync(UpcomingEpisode episode, CancellationToken ct = default);
    }

    public record DispatchResult(
        int UsersChecked,
        int CachesRefreshed,
        int CachesFailed,
        int NotificationsCreated,
        int NotificationsSuppressed);
}
