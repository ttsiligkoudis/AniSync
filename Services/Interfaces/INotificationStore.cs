using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Persistence for per-user episode-release notifications shown in the
    /// site-header bell. Backed by SQLite; safe to use as a singleton (each
    /// call opens its own pooled connection).
    /// </summary>
    public interface INotificationStore
    {
        /// <summary>
        /// Inserts a notification, no-op on (uid, anime_id, season, episode_number)
        /// duplicate. Returns true when a new row was inserted, false when the
        /// unique index suppressed it. The dispatcher relies on this idempotency
        /// — every 5-min cron tick is safe to re-run on overlapping windows.
        /// </summary>
        Task<bool> CreateAsync(NotificationRecord record);

        /// <summary>Most-recent-first, capped at <paramref name="limit"/>.</summary>
        Task<List<NotificationRecord>> ListForUserAsync(string uid, int limit = 20);

        Task<int> GetUnreadCountAsync(string uid);

        /// <summary>UPDATE gated on uid so user A can't dismiss user B's rows.</summary>
        Task<bool> MarkReadAsync(string uid, long id);

        Task<int> MarkAllReadAsync(string uid);
    }
}
