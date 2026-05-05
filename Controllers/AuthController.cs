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
        // Distinguishes a primary login from a link-additional-provider flow — both go
        // through /Auth/Callback for OAuth providers, and we need to know which on return.
        private const string OauthFlowKey = "OauthFlow";
        private const string OauthFlowLogin = "Login";
        private const string OauthFlowLink = "Link";

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
                HttpContext.Session.SetString(OauthFlowKey, OauthFlowLogin);
                return Redirect($"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=code");
            }
            else if (animeService == AnimeService.MyAnimeList)
            {
                return BeginMalOauth(OauthFlowLogin) ?? RedirectToAction("Index", "Home");
            }
            else
            {
                await _tokenService.GetAccessTokenByCredsAsync(username, password, true);

                return RedirectToAction("Index", "Home");
            }
        }

        /// <summary>
        /// Builds the MAL authorize redirect (with PKCE), tagging the session with the
        /// supplied flow ("Login" or "Link") so /Auth/Callback knows which side to land on.
        /// Returns the redirect, or a BadRequest when MAL credentials aren't configured.
        /// </summary>
        private IActionResult BeginMalOauth(string flow)
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
            HttpContext.Session.SetString(OauthFlowKey, flow);
            HttpContext.Session.SetString(MalCodeVerifierKey, verifier);

            var url =
                "https://myanimelist.net/v1/oauth2/authorize" +
                $"?response_type=code&client_id={Uri.EscapeDataString(malClientId)}" +
                $"&code_challenge={Uri.EscapeDataString(verifier)}" +
                "&code_challenge_method=plain" +
                $"&redirect_uri={Uri.EscapeDataString(malRedirectUri)}";
            return Redirect(url);
        }

        [HttpGet]
        public async Task<IActionResult> Callback(string code)
        {
            // Read flow + provider before clearing — both AniList and MAL share this URL,
            // and the link-additional-provider flow lands here too.
            var oauthService = HttpContext.Session.GetString(OauthServiceKey);
            var oauthFlow = HttpContext.Session.GetString(OauthFlowKey);
            var malVerifier = HttpContext.Session.GetString(MalCodeVerifierKey);

            HttpContext.Session.Remove(OauthServiceKey);
            HttpContext.Session.Remove(OauthFlowKey);
            HttpContext.Session.Remove(MalCodeVerifierKey);

            var isLink = oauthFlow == OauthFlowLink;
            // Capture the primary's session token *before* the login path clears it. The
            // link path needs it to find the primary's UID and attach the linked token.
            var primarySession = HttpContext.Session.GetString("AccessToken");

            if (!isLink)
                HttpContext.Session.Remove("AccessToken");

            TokenData linkedTokenData = null;
            if (oauthService == AnimeService.MyAnimeList.ToString())
                linkedTokenData = await _tokenService.GetAccessTokenByMalCodeAsync(code, malVerifier, setSession: !isLink);
            else
                linkedTokenData = await _tokenService.GetAccessTokenByCodeAsync(code, setSession: !isLink);

            if (isLink && linkedTokenData != null && !string.IsNullOrEmpty(primarySession))
                await PersistLinkedTokenAsync(primarySession, linkedTokenData);

            return RedirectToAction("Index", "Home");
        }

        /// <summary>
        /// Resolves the primary user's UID via UpsertAsync (idempotent — same UID across
        /// re-logins) and stores the freshly-exchanged linked token under it. The store
        /// keys linked tokens on (uid, service) so re-linking the same provider replaces
        /// the prior token cleanly.
        /// </summary>
        private async Task PersistLinkedTokenAsync(string primarySessionJson, TokenData linkedTokenData)
        {
            var primary = DeserializeObject<TokenData>(primarySessionJson);
            if (primary == null || primary.anonymousUser) return;
            // Don't allow linking the same service the user is logged in with — the primary
            // already covers it and silently overwriting that path would be confusing.
            if (linkedTokenData.anime_service == primary.anime_service) return;

            var uid = await _configStore.UpsertAsync(primary);
            await _configStore.SetLinkedTokenAsync(uid, new LinkedToken
            {
                Service = linkedTokenData.anime_service,
                TokenData = linkedTokenData,
                NeedsReauth = false,
            });
        }

        /// <summary>
        /// Starts an OAuth flow that will store the resulting token as a linked provider
        /// rather than the primary login. Requires the user to already be logged in.
        /// </summary>
        [HttpGet]
        public IActionResult LinkProvider(AnimeService service)
        {
            var primary = GetSessionPrimary();
            if (primary == null) return BadRequest("Log in with a primary provider first.");
            if (primary.anime_service == service) return BadRequest("This provider is already your primary account.");

            if (service == AnimeService.Anilist)
            {
                HttpContext.Session.SetString(OauthServiceKey, AnimeService.Anilist.ToString());
                HttpContext.Session.SetString(OauthFlowKey, OauthFlowLink);
                return Redirect($"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=code");
            }

            if (service == AnimeService.MyAnimeList)
            {
                return BeginMalOauth(OauthFlowLink);
            }

            // Kitsu uses password grant — UI posts to /Auth/LinkKitsu instead.
            return BadRequest("Use /Auth/LinkKitsu for Kitsu.");
        }

        /// <summary>
        /// Links a Kitsu account using the username/password grant Kitsu requires. Posted
        /// from the inline form on the configure page.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> LinkKitsu(string username, string password)
        {
            var primary = GetSessionPrimary();
            if (primary == null) return BadRequest("Log in with a primary provider first.");
            if (primary.anime_service == AnimeService.Kitsu) return BadRequest("This provider is already your primary account.");

            var linked = await _tokenService.GetAccessTokenByCredsAsync(username, password, setContext: false);
            if (linked == null || string.IsNullOrEmpty(linked.access_token))
                return BadRequest("Kitsu credentials were rejected.");

            var uid = await _configStore.UpsertAsync(primary);
            await _configStore.SetLinkedTokenAsync(uid, new LinkedToken
            {
                Service = AnimeService.Kitsu,
                TokenData = linked,
                NeedsReauth = false,
            });

            return RedirectToAction("Index", "Home");
        }

        /// <summary>
        /// Promotes a linked provider to primary on the current install. UID is preserved so
        /// existing Stremio installs keep working — only the primary slot rotates. POST to
        /// avoid CSRF-style triggers via image tags or prefetch.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SetPrimary(AnimeService service)
        {
            var primary = GetSessionPrimary();
            if (primary == null) return BadRequest("Log in with a primary provider first.");
            if (primary.anime_service == service) return BadRequest("This provider is already your primary account.");

            var uid = await _configStore.UpsertAsync(primary);
            var (newPrimary, reason) = await _configStore.SwapPrimaryAsync(uid, service);
            if (newPrimary == null)
                return BadRequest(BuildSwapErrorMessage(reason, service));

            // Push the new primary into the session so subsequent requests dispatch through
            // its service. The UID stayed the same, so install URLs continue to resolve.
            HttpContext.Session.SetString("AccessToken", SerializeObject(newPrimary));
            return RedirectToAction("Index", "Home");
        }

        private static string BuildSwapErrorMessage(string reason, AnimeService service)
        {
            // Keep the messages actionable — when we can name the fix, we do.
            return reason switch
            {
                "needs-reauth" => $"The {service} link needs re-authentication. Re-link it from the Linked Accounts section, then try again.",
                "not-linked"   => $"{service} isn't linked to this install — link it first from the Linked Accounts section.",
                "no-token"     => $"The stored {service} link is incomplete. Unlink and re-link it before promoting.",
                "collision"    => $"The {service} account is already the primary on another AniSync install. Disconnect that install before promoting it here.",
                "no-primary"   => "No primary account is set for this install — log in first.",
                _              => $"Couldn't promote {service} ({reason ?? "unknown"}).",
            };
        }

        /// <summary>
        /// Removes a linked provider for the currently-authenticated primary user. POST so
        /// it can't be triggered by a CSRF-style image-tag GET.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> UnlinkProvider(AnimeService service)
        {
            var primary = GetSessionPrimary();
            if (primary == null) return BadRequest("Log in with a primary provider first.");

            var uid = await _configStore.UpsertAsync(primary);
            await _configStore.RemoveLinkedTokenAsync(uid, service);
            return RedirectToAction("Index", "Home");
        }

        /// <summary>
        /// Reads and deserialises the primary's tokenData from the session. Returns null
        /// if there's no session, the user is anonymous, or deserialisation fails.
        /// </summary>
        private TokenData GetSessionPrimary()
        {
            var sessionStr = HttpContext.Session.GetString("AccessToken");
            if (string.IsNullOrEmpty(sessionStr)) return null;
            var token = DeserializeObject<TokenData>(sessionStr);
            return token == null || token.anonymousUser ? null : token;
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