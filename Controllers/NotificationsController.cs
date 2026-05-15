using AnimeList.Models.Api;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Cryptography;
using System.Text;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Browser-session API for the bell dropdown in the site header.
    /// Authenticated via the same cookie-rehydrated session
    /// <see cref="AnimeController"/> uses (not the X-AniSync-Config header
    /// that <see cref="UserApiController"/> requires) — these endpoints are
    /// only ever called from the layout JS running on a page the user is
    /// already signed into.
    /// </summary>
    [ApiController]
    [Route("api/v1/notifications")]
    [EnableRateLimiting("api")]
    [Tags("Notifications")]
    [Produces("application/json")]
    public class NotificationsController : ControllerBase
    {
        private readonly INotificationStore _store;
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;

        public NotificationsController(
            INotificationStore store,
            ITokenService tokenService,
            IConfigStore configStore)
        {
            _store = store;
            _tokenService = tokenService;
            _configStore = configStore;
        }

        private async Task<string> ResolveCurrentUidAsync()
        {
            var token = await _tokenService.GetAccessTokenAsync();
            if (token == null || token.anonymousUser) return null;
            var (uid, _) = await _configStore.FindUidByIdentityAsync(token);
            return uid;
        }

        /// <summary>List the most recent notifications for the signed-in user.</summary>
        [HttpGet]
        public async Task<IActionResult> List()
        {
            var uid = await ResolveCurrentUidAsync();
            if (uid == null) return Unauthorized(new ApiError("not signed in"));
            var items = await _store.ListForUserAsync(uid, 20);
            return new JsonResult(new { items });
        }

        /// <summary>
        /// Unread count for the badge. Anonymous callers get <c>{count: 0}</c>
        /// (not 401) — the bell JS polls this every 60s and a 401 storm in
        /// logs from logged-out tabs would be noise.
        /// </summary>
        [HttpGet("count")]
        public async Task<IActionResult> Count()
        {
            var uid = await ResolveCurrentUidAsync();
            if (uid == null) return new JsonResult(new { count = 0 });
            var count = await _store.GetUnreadCountAsync(uid);
            return new JsonResult(new { count });
        }

        [HttpPost("{id:long}/read")]
        public async Task<IActionResult> MarkRead(long id)
        {
            var uid = await ResolveCurrentUidAsync();
            if (uid == null) return Unauthorized(new ApiError("not signed in"));
            var ok = await _store.MarkReadAsync(uid, id);
            return ok ? Ok() : NotFound();
        }

        [HttpPost("read-all")]
        public async Task<IActionResult> MarkAllRead()
        {
            var uid = await ResolveCurrentUidAsync();
            if (uid == null) return Unauthorized(new ApiError("not signed in"));
            var marked = await _store.MarkAllReadAsync(uid);
            return new JsonResult(new { marked });
        }
    }

    /// <summary>
    /// Internal entry-point for the <c>cf-episode-notifier</c> Cloudflare
    /// Worker's cron tick. Intentionally outside the
    /// <c>EnableRateLimiting("api")</c> partition so a 5-min worker doesn't
    /// throttle itself; auth is the shared-secret header.
    /// </summary>
    [ApiController]
    [Route("api/v1/cron")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class CronController : ControllerBase
    {
        private readonly IEpisodeNotificationDispatcher _dispatcher;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CronController> _logger;

        public CronController(
            IEpisodeNotificationDispatcher dispatcher,
            IConfiguration configuration,
            ILogger<CronController> logger)
        {
            _dispatcher = dispatcher;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpPost("check-releases")]
        public async Task<IActionResult> CheckReleases(CancellationToken ct)
        {
            var expected = _configuration["ANISYNC_CRON_SECRET"]
                ?? Environment.GetEnvironmentVariable("ANISYNC_CRON_SECRET");
            if (string.IsNullOrEmpty(expected))
            {
                _logger.LogError("CronController.CheckReleases invoked with no ANISYNC_CRON_SECRET configured");
                return StatusCode(500, new ApiError("cron secret not configured"));
            }

            var provided = Request.Headers["X-Cron-Secret"].FirstOrDefault() ?? string.Empty;
            // Constant-time compare so timing attacks can't recover the secret
            // byte-by-byte off the public endpoint.
            var providedBytes = Encoding.UTF8.GetBytes(provided);
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            if (providedBytes.Length != expectedBytes.Length
                || !CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            {
                return Unauthorized();
            }

            try
            {
                var result = await _dispatcher.RunAsync(ct);
                _logger.LogInformation(
                    "cron tick: refreshed={Refreshed} failed={Failed} airing={Airing} created={Created} suppressed={Suppressed}",
                    result.CachesRefreshed, result.CachesFailed, result.AiringChecked,
                    result.NotificationsCreated, result.NotificationsSuppressed);
                return new JsonResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "cron tick failed");
                return StatusCode(500, new ApiError("dispatcher failed"));
            }
        }
    }
}
