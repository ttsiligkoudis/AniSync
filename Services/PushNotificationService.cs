using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json;
using WebPush;

namespace AnimeList.Services
{
    /// <summary>
    /// VAPID-signed Web Push delivery via the WebPush NuGet package.
    /// Reads VAPID keys + subject from configuration; when any of those
    /// is missing the service silently disables itself (every SendAsync
    /// becomes a no-op). Generate keys with the helper at the bottom
    /// of this file (a one-liner you can run via
    /// `dotnet run --project AniSync -- --print-vapid-keys` or any
    /// VAPID generator tool).
    /// </summary>
    public class PushNotificationService : IPushNotificationService
    {
        private readonly IPushSubscriptionStore _subscriptions;
        private readonly ILogger<PushNotificationService> _logger;
        private readonly VapidDetails _vapid;
        private readonly WebPushClient _client;

        public PushNotificationService(
            IConfiguration configuration,
            IPushSubscriptionStore subscriptions,
            ILogger<PushNotificationService> logger)
        {
            _subscriptions = subscriptions;
            _logger = logger;

            var publicKey = configuration["Push:VapidPublicKey"]
                ?? Environment.GetEnvironmentVariable("Push__VapidPublicKey");
            var privateKey = configuration["Push:VapidPrivateKey"]
                ?? Environment.GetEnvironmentVariable("Push__VapidPrivateKey");
            // Subject is the mailto: or https: URL push providers
            // contact if your VAPID keys start signing bad requests.
            // Defaults to an obvious "set me" placeholder so the upstream
            // gets a recognizable identifier even when admins forget.
            var subject = configuration["Push:VapidSubject"]
                ?? Environment.GetEnvironmentVariable("Push__VapidSubject")
                ?? "mailto:noreply@anisync.local";

            if (!string.IsNullOrEmpty(publicKey) && !string.IsNullOrEmpty(privateKey))
            {
                try
                {
                    _vapid = new VapidDetails(subject, publicKey, privateKey);
                    _client = new WebPushClient();
                    PublicKey = publicKey;
                    IsEnabled = true;
                    _logger.LogInformation("PushNotificationService enabled (subject={Subject})", subject);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PushNotificationService: VAPID setup failed; push disabled");
                }
            }
            else
            {
                _logger.LogInformation(
                    "PushNotificationService disabled (Push:VapidPublicKey + Push:VapidPrivateKey not configured)");
            }
        }

        public bool IsEnabled { get; }
        public string PublicKey { get; }

        public async Task SendAsync(string uid, NotificationRecord notification)
        {
            if (!IsEnabled || string.IsNullOrEmpty(uid) || notification == null) return;

            List<PushSubscriptionRecord> subs;
            try { subs = await _subscriptions.ListForUserAsync(uid); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PushNotificationService: subscription lookup failed for {Uid}", uid);
                return;
            }
            if (subs.Count == 0) return;

            // Payload picked up by the service worker's `push` handler
            // (wwwroot/sw.js). Stays under Web Push's 4 KiB encrypted
            // limit easily — title + body + URL + thumbnail URL is at
            // most a few hundred bytes.
            var payload = JsonConvert.SerializeObject(new
            {
                title = notification.AnimeTitle,
                body = $"Episode {notification.EpisodeNumber} just aired",
                icon = notification.ThumbnailUrl,
                url = notification.LinkPath,
                // tag dedups notifications client-side — re-pushing the
                // same airing collapses to one OS notification instead
                // of stacking.
                tag = $"anisync:{notification.AnimeId}:{notification.EpisodeNumber}",
            });

            foreach (var sub in subs)
            {
                try
                {
                    var pushSub = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                    await _client.SendNotificationAsync(pushSub, payload, _vapid);
                }
                catch (WebPushException ex) when (
                    ex.StatusCode == System.Net.HttpStatusCode.Gone
                    || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // 410 Gone / 404 Not Found = the browser revoked
                    // this subscription (user cleared site data,
                    // uninstalled the PWA, etc.). Prune it so we stop
                    // wasting calls on a dead endpoint.
                    try { await _subscriptions.DeleteAsync(sub.Id); } catch { /* best-effort */ }
                    _logger.LogInformation(
                        "PushNotificationService pruned dead subscription {Id} for {Uid} (status={Status})",
                        sub.Id, uid, ex.StatusCode);
                }
                catch (Exception ex)
                {
                    // Other failures (rate limits, transient provider
                    // outages) — log + carry on. The notifications row
                    // is already in the DB; the user will see it via
                    // the bell whenever they next visit even if no
                    // push actually lands.
                    _logger.LogWarning(ex,
                        "PushNotificationService: send to {Endpoint} failed",
                        sub.Endpoint);
                }
            }
        }
    }
}
