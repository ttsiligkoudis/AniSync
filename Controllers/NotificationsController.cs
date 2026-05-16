using AnimeList.Models.Api;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
        private readonly IAnimeScheduleService _schedule;

        public NotificationsController(
            INotificationStore store,
            ITokenService tokenService,
            IConfigStore configStore,
            IAnimeScheduleService schedule)
        {
            _store = store;
            _tokenService = tokenService;
            _configStore = configStore;
            _schedule = schedule;
        }

        private async Task<string> ResolveCurrentUidAsync()
        {
            var (_, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
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
        /// Unread count for the badge plus the Unix-seconds timestamp of the
        /// next future episode in the global airing schedule (not filtered to
        /// the user's Watching list — it's the same value for every signed-in
        /// user). The browser uses <c>nextAiringAt</c> to schedule one
        /// <c>setTimeout</c> per known release window rather than blind
        /// polling. Anonymous callers get a zero count (not 401) so a 401
        /// storm in logs from logged-out background tabs doesn't happen.
        /// </summary>
        [HttpGet("count")]
        public async Task<IActionResult> Count()
        {
            var nextAiringAt = _schedule.GetNextAiringAt();
            var uid = await ResolveCurrentUidAsync();
            if (uid == null) return new JsonResult(new { count = 0, nextAiringAt });
            var count = await _store.GetUnreadCountAsync(uid);
            return new JsonResult(new { count, nextAiringAt });
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
}
