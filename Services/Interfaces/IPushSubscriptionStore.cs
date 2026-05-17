using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Persistence for browser Web Push subscriptions. One row per
    /// (uid, endpoint) pair — a user can subscribe from multiple
    /// browsers / devices and all of them will receive pushes when
    /// the dispatcher creates a notification for that uid.
    /// </summary>
    public interface IPushSubscriptionStore
    {
        /// <summary>
        /// Inserts or updates the subscription. Repeated subscribes from
        /// the same browser (same endpoint) refresh the keys + timestamp
        /// rather than creating duplicates.
        /// </summary>
        Task UpsertAsync(PushSubscriptionRecord record);

        /// <summary>
        /// All currently-stored subscriptions for a user. The dispatcher
        /// fans pushes out to every entry on each notification create.
        /// </summary>
        Task<List<PushSubscriptionRecord>> ListForUserAsync(string uid);

        /// <summary>
        /// Removes a subscription. Called when the user explicitly
        /// disables push, or when a push attempt returns 410 Gone /
        /// 404 Not Found from the upstream push provider (the
        /// browser has revoked the subscription and we shouldn't
        /// keep retrying).
        /// </summary>
        Task<bool> RemoveByEndpointAsync(string uid, string endpoint);

        /// <summary>
        /// Variant for the cleanup path — the dispatcher loop knows
        /// the row id from <see cref="ListForUserAsync"/> so it can
        /// drop a stale subscription without re-resolving uid +
        /// endpoint.
        /// </summary>
        Task<bool> DeleteAsync(long id);

        /// <summary>Returns true when the user has at least one active subscription.</summary>
        Task<bool> HasAnyAsync(string uid);
    }
}
