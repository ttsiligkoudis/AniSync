using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Sends Web Push payloads to every subscribed browser for a uid
    /// when the dispatcher creates a notification. Configured via
    /// Push:VapidPublicKey / Push:VapidPrivateKey / Push:VapidSubject
    /// in appsettings (or env vars); when keys are absent the service
    /// disables itself and every Send call is a no-op so the rest of
    /// the system keeps working without push.
    /// </summary>
    public interface IPushNotificationService
    {
        /// <summary>True when VAPID keys are configured; false otherwise.</summary>
        bool IsEnabled { get; }

        /// <summary>Base64-URL-safe VAPID public key, or null when push is disabled. Surfaced to the client via /api/v1/push/vapid-key.</summary>
        string PublicKey { get; }

        /// <summary>
        /// Pushes a notification payload to every browser subscribed for
        /// <paramref name="uid"/>. Stale subscriptions (the upstream
        /// returns 410 Gone / 404 Not Found) are pruned from the store.
        /// Fire-and-forget from the dispatcher's perspective: failures
        /// are logged but don't surface as exceptions, so a flaky push
        /// provider can't break notification creation.
        /// </summary>
        Task SendAsync(string uid, NotificationRecord notification);
    }
}
