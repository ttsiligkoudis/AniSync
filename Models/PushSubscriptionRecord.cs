namespace AnimeList.Models
{
    /// <summary>
    /// One browser's Web Push subscription. The (uid, endpoint) tuple
    /// is unique — a user with multiple devices gets one row per
    /// device and all of them receive pushes on episode dispatch.
    /// <c>p256dh</c> + <c>auth</c> are the keys the push provider
    /// (Chrome's FCM endpoint, Firefox's autopush, etc.) uses to
    /// derive the per-message encryption secret.
    /// </summary>
    public class PushSubscriptionRecord
    {
        public long Id { get; set; }
        public string Uid { get; set; }
        public string Endpoint { get; set; }
        public string P256dh { get; set; }
        public string Auth { get; set; }
        public string UserAgent { get; set; }
        public long CreatedAt { get; set; }
    }
}
