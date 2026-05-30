using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Trakt OAuth connect / disconnect for the video section. Trakt attaches to
    /// the user's existing AniSync account (config row) as a dedicated
    /// credential — so connecting requires a signed-in, non-anonymous session.
    /// Mirrors the CSRF-state handling of the anime OAuth flow in AuthController.
    /// </summary>
    public class TraktController : Controller
    {
        private readonly ITraktService _trakt;
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;
        private readonly ILogger<TraktController> _logger;

        private const string StateKey = "TraktOauthState";
        private const string ReturnUrlKey = "TraktReturnUrl";

        public TraktController(
            ITraktService trakt,
            ITokenService tokenService,
            IConfigStore configStore,
            ILogger<TraktController> logger)
        {
            _trakt = trakt;
            _tokenService = tokenService;
            _configStore = configStore;
            _logger = logger;
        }

        [HttpGet("/trakt/connect")]
        public async Task<IActionResult> Connect(string returnUrl = null)
        {
            if (!_trakt.IsConfigured)
            {
                _logger.LogWarning("Trakt connect attempted but Trakt is not configured.");
                return Redirect(SafeReturn(returnUrl));
            }

            // Connecting writes onto the user's config row, so they must be
            // signed in with a real (non-anonymous) account first.
            var (token, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            _ = token;
            if (string.IsNullOrEmpty(uid))
            {
                return Redirect("/account?trakt=signin");
            }

            var state = GenerateCodeVerifier();
            HttpContext.Session.SetString(StateKey, state);
            if (!string.IsNullOrEmpty(returnUrl))
                HttpContext.Session.SetString(ReturnUrlKey, returnUrl);
            else
                HttpContext.Session.Remove(ReturnUrlKey);

            return Redirect(_trakt.BuildAuthorizeUrl(state));
        }

        [HttpGet("/trakt/callback")]
        public async Task<IActionResult> Callback(string code, string state = null)
        {
            var expectedState = HttpContext.Session.GetString(StateKey);
            var returnUrl = HttpContext.Session.GetString(ReturnUrlKey);
            HttpContext.Session.Remove(StateKey);
            HttpContext.Session.Remove(ReturnUrlKey);

            // CSRF gate — the state we minted must round-trip intact.
            if (string.IsNullOrEmpty(expectedState)
                || !string.Equals(expectedState, state, StringComparison.Ordinal))
            {
                _logger.LogWarning("Trakt callback rejected: state mismatch.");
                return Redirect(SafeReturn(returnUrl) + "?trakt=error");
            }

            var (token, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            _ = token;
            if (string.IsNullOrEmpty(uid))
            {
                return Redirect("/account?trakt=signin");
            }

            if (string.IsNullOrEmpty(code))
            {
                return Redirect(SafeReturn(returnUrl) + "?trakt=denied");
            }

            var exchanged = await _trakt.ExchangeCodeAsync(code);
            if (exchanged == null || !exchanged.Connected)
            {
                return Redirect(SafeReturn(returnUrl) + "?trakt=error");
            }

            await _configStore.SetTraktTokenAsync(uid, exchanged);
            _logger.LogInformation("Trakt connected for uid {Uid} (user={User}).", uid, exchanged.username);
            return Redirect(SafeReturn(returnUrl) + "?trakt=connected");
        }

        [HttpPost("/trakt/disconnect")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Disconnect(string returnUrl = null)
        {
            var (token, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            _ = token;
            if (!string.IsNullOrEmpty(uid))
            {
                await _configStore.ClearTraktTokenAsync(uid);
                _logger.LogInformation("Trakt disconnected for uid {Uid}.", uid);
            }
            return Redirect(SafeReturn(returnUrl));
        }

        // Only allow same-site relative return paths so the OAuth round-trip
        // can't be used as an open redirect. Defaults to the movies browse.
        private static string SafeReturn(string returnUrl)
        {
            if (!string.IsNullOrEmpty(returnUrl)
                && returnUrl.StartsWith('/')
                && !returnUrl.StartsWith("//"))
            {
                return returnUrl;
            }
            return "/movies";
        }
    }
}
