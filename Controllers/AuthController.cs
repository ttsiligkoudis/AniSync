using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    public class AuthController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfigStore _configStore;
        private readonly IConfiguration _configuration;

        // Keys we stash in the session while a callback-based OAuth flow is in flight.
        // We need to remember which provider initiated the flow (the callback URL is shared)
        // and, for MAL specifically, the PKCE verifier.
        private const string OauthServiceKey = "OauthService";
        private const string MalCodeVerifierKey = "MalCodeVerifier";

        public AuthController(ITokenService tokenService, IHttpContextAccessor httpContextAccessor, IConfigStore configStore, IConfiguration configuration)
        {
            _tokenService = tokenService;
            _httpContextAccessor = httpContextAccessor;
            _configStore = configStore;
            _configuration = configuration;
        }

        [HttpGet]
        public async Task<IActionResult> Login(AnimeService? animeService = null, string username = null, string password = null, bool anonymous = false)
        {
            if (anonymous)
            {
                var tokenData = new TokenData
                {
                    anime_service = animeService ?? AnimeService.Kitsu
                };

                _httpContextAccessor.HttpContext.Session.SetString("AccessToken", SerializeObject(tokenData));

                return RedirectToAction("Index", "Home");
            }
            else if (animeService == AnimeService.Anilist)
            {
                HttpContext.Session.SetString(OauthServiceKey, AnimeService.Anilist.ToString());
                return Redirect($"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=code");
            }
            else if (animeService == AnimeService.MyAnimeList)
            {
                var malClientId = _configuration["Mal:ClientId"];
                var malRedirectUri = _configuration["Mal:RedirectUri"];
                if (string.IsNullOrEmpty(malClientId) || string.IsNullOrEmpty(malRedirectUri))
                    return BadRequest("MyAnimeList client is not configured. Set Mal:ClientId and Mal:RedirectUri.");

                // MAL only supports code_challenge_method=plain, so the verifier and the
                // challenge are the same value. Stash the verifier in the session so the
                // shared /Auth/Callback can finish the exchange.
                var verifier = GenerateCodeVerifier();
                HttpContext.Session.SetString(OauthServiceKey, AnimeService.MyAnimeList.ToString());
                HttpContext.Session.SetString(MalCodeVerifierKey, verifier);

                var url =
                    "https://myanimelist.net/v1/oauth2/authorize" +
                    $"?response_type=code&client_id={Uri.EscapeDataString(malClientId)}" +
                    $"&code_challenge={Uri.EscapeDataString(verifier)}" +
                    "&code_challenge_method=plain" +
                    $"&redirect_uri={Uri.EscapeDataString(malRedirectUri)}";
                return Redirect(url);
            }
            else
            {
                await _tokenService.GetAccessTokenByCredsAsync(username, password, true);

                return RedirectToAction("Index", "Home");
            }
        }

        [HttpGet]
        public async Task<IActionResult> Callback(string code)
        {
            // Read which provider initiated the flow before clearing the session — both
            // AniList and MAL hit this endpoint with the same query shape.
            var oauthService = HttpContext.Session.GetString(OauthServiceKey);
            var malVerifier = HttpContext.Session.GetString(MalCodeVerifierKey);

            HttpContext.Session.Remove("AccessToken");
            HttpContext.Session.Remove(OauthServiceKey);
            HttpContext.Session.Remove(MalCodeVerifierKey);

            if (oauthService == AnimeService.MyAnimeList.ToString())
            {
                await _tokenService.GetAccessTokenByMalCodeAsync(code, malVerifier);
            }
            else
            {
                await _tokenService.GetAccessTokenByCodeAsync(code);
            }

            return RedirectToAction("Index", "Home");
        }

        public async Task<IActionResult> Logout()
        {
            // Read the session token directly so we can identify the user without going
            // through GetAccessTokenAsync (which would refresh the upstream token and write
            // back to the row we're about to delete).
            var sessionStr = HttpContext.Session.GetString("AccessToken");
            if (!string.IsNullOrEmpty(sessionStr))
            {
                var tokenData = DeserializeObject<TokenData>(sessionStr);
                if (tokenData != null && !tokenData.anonymousUser)
                    await _configStore.DeleteByUserAsync(tokenData);
            }

            await _tokenService.RemoveCachedUser();

            return RedirectToAction("Index", "Home");
        }
    }
}