using AnimeList.Models;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Cookie-session Trakt write surface for the site UI (video section):
    /// mark-watched, watchlist add/remove, and live scrobble. Mirrors the
    /// /api/library/* convention — a plain MVC controller behind CsrfOrAjaxFilter,
    /// so callers send <c>X-Requested-With: XMLHttpRequest</c> (the watch/browse
    /// JS does). Auth is the session: each call resolves the current UID and
    /// no-ops with reason="not_connected" when Trakt isn't linked.
    /// </summary>
    [Route("api/trakt")]
    public class TraktApiController : Controller
    {
        private readonly ITraktService _trakt;
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;

        public TraktApiController(ITraktService trakt, ITokenService tokenService, IConfigStore configStore)
        {
            _trakt = trakt;
            _tokenService = tokenService;
            _configStore = configStore;
        }

        [HttpPost("history")]
        public async Task<IActionResult> History([FromBody] TraktActionRequest req)
        {
            var uid = await CurrentUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotConnected();
            var ok = await _trakt.AddToHistoryAsync(uid, req.type, req.id, req.season, req.episode);
            return Json(new { success = ok });
        }

        [HttpPost("watchlist")]
        public async Task<IActionResult> Watchlist([FromBody] TraktActionRequest req)
        {
            var uid = await CurrentUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotConnected();

            var remove = string.Equals(req.action, "remove", StringComparison.OrdinalIgnoreCase);
            var ok = remove
                ? await _trakt.RemoveFromWatchlistAsync(uid, req.type, req.id)
                : await _trakt.AddToWatchlistAsync(uid, req.type, req.id);
            return Json(new { success = ok, inWatchlist = ok && !remove });
        }

        [HttpPost("scrobble")]
        public async Task<IActionResult> Scrobble([FromBody] TraktActionRequest req)
        {
            var uid = await CurrentUidAsync();
            if (string.IsNullOrEmpty(uid)) return NotConnected();
            var ok = await _trakt.ScrobbleAsync(uid, req.action, req.type, req.id, req.season, req.episode, req.progress ?? 0);
            return Json(new { success = ok });
        }

        // Resolves the current session's UID only when Trakt is actually
        // connected — keeps every write a cheap no-op for anonymous / unlinked
        // sessions without a wasted API call.
        private async Task<string> CurrentUidAsync()
        {
            if (!_trakt.IsConfigured) return null;
            var (token, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            _ = token;
            if (string.IsNullOrEmpty(uid)) return null;
            var trakt = await _configStore.GetTraktTokenAsync(uid);
            return trakt?.Connected == true ? uid : null;
        }

        private IActionResult NotConnected() =>
            Json(new { success = false, reason = "not_connected" });
    }
}
