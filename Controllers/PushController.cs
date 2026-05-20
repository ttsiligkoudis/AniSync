using AnimeList.Models;
using AnimeList.Models.Api;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Browser Web Push subscription management for the bell + the
    /// notifications page. Session-authenticated like
    /// <see cref="NotificationsController"/> — the client subscribes
    /// from a logged-in page and the subscription gets stored against
    /// that uid so the dispatcher can fan pushes to it on episode
    /// notification creation.
    /// </summary>
    [ApiController]
    [Route("api/v1/push")]
    [EnableRateLimiting("api")]
    [Tags("Push")]
    [Produces("application/json")]
    public class PushController : ControllerBase
    {
        private readonly IPushNotificationService _push;
        private readonly IPushSubscriptionStore _subscriptions;
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;

        public PushController(
            IPushNotificationService push,
            IPushSubscriptionStore subscriptions,
            ITokenService tokenService,
            IConfigStore configStore)
        {
            _push = push;
            _subscriptions = subscriptions;
            _tokenService = tokenService;
            _configStore = configStore;
        }

        /// <summary>
        /// VAPID public key the client needs to call <c>PushManager.subscribe</c>.
        /// Returns <c>{enabled: false}</c> when the deployment hasn't
        /// configured VAPID keys (push features hidden client-side).
        /// </summary>
        [HttpGet("vapid-key")]
        public IActionResult VapidKey()
        {
            return _push.IsEnabled
                ? new JsonResult(new { enabled = true, publicKey = _push.PublicKey })
                : new JsonResult(new { enabled = false });
        }

        /// <summary>
        /// Whether the current session has any active push subscription
        /// on this uid. The toggle UI uses this to decide whether to
        /// show "Enable" or "Disable".
        /// </summary>
        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            if (!_push.IsEnabled)
                return new JsonResult(new { enabled = false, subscribed = false });
            var (_, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            if (uid == null)
                return new JsonResult(new { enabled = true, subscribed = false });
            var any = await _subscriptions.HasAnyAsync(uid);
            return new JsonResult(new { enabled = true, subscribed = any });
        }

        /// <summary>
        /// Stores or updates a browser's push subscription against the
        /// current uid. Body matches what <c>PushSubscription.toJSON()</c>
        /// produces on the client: <c>{endpoint, keys: {p256dh, auth}}</c>.
        /// </summary>
        [HttpPost("subscribe")]
        public async Task<IActionResult> Subscribe([FromBody] PushSubscribeRequest body)
        {
            if (!_push.IsEnabled)
                return BadRequest(new ApiError("push not enabled on this deployment"));
            var (_, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            if (uid == null)
                return Unauthorized(new ApiError("not signed in"));
            if (body == null
                || string.IsNullOrEmpty(body.Endpoint)
                || body.Keys == null
                || string.IsNullOrEmpty(body.Keys.P256dh)
                || string.IsNullOrEmpty(body.Keys.Auth))
            {
                return BadRequest(new ApiError("invalid subscription payload"));
            }

            await _subscriptions.UpsertAsync(new PushSubscriptionRecord
            {
                Uid = uid,
                Endpoint = body.Endpoint,
                P256dh = body.Keys.P256dh,
                Auth = body.Keys.Auth,
                UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            return Ok(new { ok = true });
        }

        /// <summary>
        /// Removes a subscription. Browsers call this when the user
        /// disables notifications from the bell UI.
        /// </summary>
        [HttpPost("unsubscribe")]
        public async Task<IActionResult> Unsubscribe([FromBody] PushUnsubscribeRequest body)
        {
            var (_, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            if (uid == null)
                return Unauthorized(new ApiError("not signed in"));
            if (body == null || string.IsNullOrEmpty(body.Endpoint))
                return BadRequest(new ApiError("endpoint required"));
            await _subscriptions.RemoveByEndpointAsync(uid, body.Endpoint);
            return Ok(new { ok = true });
        }
    }

    public record PushSubscribeRequest(string Endpoint, PushSubscribeKeys Keys);
    public record PushSubscribeKeys(string P256dh, string Auth);
    public record PushUnsubscribeRequest(string Endpoint);
}
