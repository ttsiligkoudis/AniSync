using AnimeList.Models;
using AnimeList.Services;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace AnimeList.Controllers
{
    // The Blazor Web head runs app.UseAntiforgery() (required by Razor Components).
    // In .NET 8+ that middleware auto-requires an antiforgery token for controller
    // actions that bind [FromForm] (LoginKitsu / Register here), which both breaks
    // form-field binding and would 400 the post. The maui head deliberately drops
    // UI-CSRF (see Program.cs) and authenticates the same way UserApiController does
    // — the OAuth `state` round-trip already guards the login flows against CSRF —
    // so opt these actions out of antiforgery, mirroring UserApiController.
    [IgnoreAntiforgeryToken]
    public class AuthController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfigStore _configStore;
        private readonly IConfiguration _configuration;
        private readonly ISyncService _syncService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly ITraktService _traktService;
        private readonly IAnimeMappingService _mappingService;
        private readonly INativeAuthCodeStore _nativeCodes;
        private readonly ILogger<AuthController> _logger;

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
        // Survives the OAuth round-trip so /Auth/Callback can hand the user back to
        // whichever AniSync page started the link flow (/configure or /stremio).
        private const string OauthReturnUrlKey = "OauthReturnUrl";
        // CSRF defence for the OAuth flows. A cryptographically-random value is minted
        // on every authorize redirect, stashed in the session, and echoed back by the
        // provider on /Auth/Callback. A missing or mismatched value means the callback
        // wasn't initiated by this session (login-CSRF / account-fixation), so we refuse
        // to exchange the code. Both AniList and MAL support the standard `state` param.
        private const string OauthStateKey = "OauthState";
        // Marks a login started from the native (MAUI) app via WebAuthenticator. On the
        // callback we don't seed a browser localStorage credential (there's no app DOM to
        // seed) — instead we mint a one-time code and redirect to the app's anisync:// deep
        // link, which the app exchanges for its config segment. See INativeAuthCodeStore.
        private const string OauthNativeKey = "OauthNative";
        private const string NativeCallbackScheme = "anisync://auth";

        // Per-(IP, email) in-flight guard for the Kitsu signup. Two browser tabs
        // submitting the same form within a few ms could otherwise both pass Kitsu's
        // duplicate-email check (their write hasn't propagated yet) and both try to
        // password-grant; one would land in a broken state. TryAdd is atomic, the
        // finally below clears the slot so a retry after a real failure isn't blocked.
        // ConcurrentDictionary doesn't need a TTL — bounded by the network round-trip
        // to Kitsu, ~1s, and the worst case is a stale entry held by a crashed worker
        // (one process restart later it's gone).
        private static readonly ConcurrentDictionary<string, byte> _registerInFlight = new();

        /// <summary>
        /// Returns a redirect to <paramref name="returnUrl"/> when it's a safe same-app
        /// local URL, otherwise the standard /Home/Configure redirect. Every linked-
        /// account action takes a returnUrl so the user lands back on whichever page
        /// (/configure or /stremio) they triggered the action from — the partials are
        /// rendered on both surfaces and would otherwise always bounce to /configure.
        /// </summary>
        private IActionResult RedirectToReturnUrlOrConfigure(string returnUrl)
        {
            var dest = (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) ? returnUrl : "/configure";
            return SeedConfigAndRedirect(dest);
        }

        /// <summary>
        /// Routes a terminal auth redirect through the Web head's <c>/auth/complete</c>
        /// bridge so the Blazor thin client gets the <c>X-AniSync-Config</c> credential
        /// seeded into localStorage from the freshly-established session before it
        /// renders, then lands on <paramref name="localReturnUrl"/>. The web app kept
        /// the UID in an HttpOnly cookie and server-rendered the install URL; this
        /// replica holds the config segment client-side instead (see Program.cs).
        /// <paramref name="localReturnUrl"/> is always an already-validated local URL.
        /// </summary>
        private IActionResult SeedConfigAndRedirect(string localReturnUrl)
            => Redirect($"/auth/complete?returnUrl={Uri.EscapeDataString(localReturnUrl)}");

        /// <summary>
        /// Login / Register variant of <see cref="RedirectToReturnUrlOrConfigure"/> —
        /// the same Url.IsLocalUrl gate but defaults to the home dashboard rather than
        /// /configure. Used by Login + Register + the OAuth Callback's login branch so
        /// a freshly-authenticated user lands somewhere generic by default, with
        /// /configure (or /account, or any other valid local URL) as an opt-in
        /// override the caller threads in.
        /// </summary>
        private IActionResult RedirectToReturnUrlOrHome(string returnUrl)
        {
            var dest = (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) ? returnUrl : "/";
            return SeedConfigAndRedirect(dest);
        }

        public AuthController(ITokenService tokenService, IHttpContextAccessor httpContextAccessor,
            IConfigStore configStore, IConfiguration configuration, ISyncService syncService,
            IAnilistService anilistService, IKitsuService kitsuService, IMalService malService,
            ITraktService traktService, IAnimeMappingService mappingService,
            INativeAuthCodeStore nativeCodes, ILogger<AuthController> logger)
        {
            _tokenService = tokenService;
            _httpContextAccessor = httpContextAccessor;
            _configStore = configStore;
            _configuration = configuration;
            _syncService = syncService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _traktService = traktService;
            _mappingService = mappingService;
            _nativeCodes = nativeCodes;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Login(AnimeService? animeService = null, bool anonymous = false, string returnUrl = null, bool native = false)
        {
            // Already-authenticated short-circuit. Re-running the OAuth dance for a logged-in
            // user would either silently replace their primary (login flow with `state` round-
            // trip) or refresh tokens they already have. Send them to a sensible landing and
            // let them /Auth/Logout first if they actually meant to switch accounts.
            // Anonymous users (anime-service preference picked but no real account) still pass
            // through — they DO need /Auth/Login to upgrade into a real session.
            if (GetSessionPrimary() != null)
                return RedirectToReturnUrlOrHome(returnUrl);

            // Stash the post-login destination on the session for the OAuth
            // branches (AniList / MAL) so /Auth/Callback can read + honour
            // it on return. Same key the link-additional-provider flow uses;
            // Callback always reads-and-clears it regardless of which flow
            // populated it.
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                HttpContext.Session.SetString(OauthReturnUrlKey, returnUrl);
            else
                HttpContext.Session.Remove(OauthReturnUrlKey);

            // Native (MAUI) logins run this flow inside the app's system browser; the
            // callback hands the result back via the anisync:// deep link instead of the
            // web localStorage-seeding bridge. Stash the flag for Callback to read.
            if (native)
                HttpContext.Session.SetString(OauthNativeKey, "1");
            else
                HttpContext.Session.Remove(OauthNativeKey);

            if (anonymous)
            {
                var tokenData = new TokenData
                {
                    anime_service = animeService ?? AnimeService.Kitsu
                };

                _httpContextAccessor.HttpContext.Session.SetString("AccessToken", SerializeObject(tokenData));

                return RedirectToReturnUrlOrHome(returnUrl);
            }
            if (animeService == AnimeService.Anilist)
            {
                HttpContext.Session.SetString(OauthServiceKey, AnimeService.Anilist.ToString());
                HttpContext.Session.SetString(OauthFlowKey, OauthFlowLogin);
                var clientId = _configuration["Anilist:ClientId"];
                var state = BeginOauthState();
                return Redirect($"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=code&state={Uri.EscapeDataString(state)}");
            }
            if (animeService == AnimeService.MyAnimeList)
            {
                return BeginMalOauth(OauthFlowLogin) ?? RedirectToReturnUrlOrHome(returnUrl);
            }
            if (animeService == AnimeService.Trakt)
            {
                return BeginTraktOauth(OauthFlowLogin) ?? RedirectToReturnUrlOrHome(returnUrl);
            }
            // Kitsu uses the password grant — the form on /configure (and /account)
            // POSTs straight to /Auth/LoginKitsu so the credentials never appear in
            // a URL (where they'd otherwise leak into access logs, browser history,
            // and Referer headers). A bare GET here (typed URL, refreshed callback)
            // just lands the user back on a sensible page.
            return RedirectToReturnUrlOrHome(returnUrl);
        }

        /// <summary>
        /// Kitsu credential login. Lives as a POST so the username + password ride
        /// in the request body rather than the URL — GET credential params leak
        /// into proxy access logs, browser history, and the Referer header on the
        /// next outbound navigation. The session-cookie-bound antiforgery filter
        /// (Program.cs) enforces same-origin via the form's __RequestVerificationToken.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> LoginKitsu([FromForm] string username, [FromForm] string password, [FromForm] string returnUrl = null)
        {
            // Mirror the Login short-circuit: a stale tab posting these creds after
            // the user signed in elsewhere shouldn't replace their primary.
            if (GetSessionPrimary() != null)
                return RedirectToReturnUrlOrHome(returnUrl);

            await _tokenService.GetAccessTokenByCredsAsync(username, password, true);
            await EnsurePrimaryPersistedAsync();

            return RedirectToReturnUrlOrHome(returnUrl);
        }

        /// <summary>
        /// Mints a fresh cryptographically-random OAuth <c>state</c> value, stashes it on
        /// the session, and returns it for embedding in the authorize redirect.
        /// <see cref="Callback"/> validates the echoed value before exchanging the code.
        /// Reuses <see cref="Utils.GenerateCodeVerifier"/> (64 bytes of CSPRNG, base64url)
        /// since a random URL-safe token is exactly what a state value needs to be.
        /// </summary>
        private string BeginOauthState()
        {
            var state = GenerateCodeVerifier();
            HttpContext.Session.SetString(OauthStateKey, state);
            return state;
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
            var state = BeginOauthState();

            var url =
                "https://myanimelist.net/v1/oauth2/authorize" +
                $"?response_type=code&client_id={Uri.EscapeDataString(malClientId)}" +
                $"&code_challenge={Uri.EscapeDataString(verifier)}" +
                "&code_challenge_method=plain" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&redirect_uri={Uri.EscapeDataString(malRedirectUri)}";
            return Redirect(url);
        }

        /// <summary>
        /// Builds the Trakt authorize redirect, tagging the session with the supplied flow
        /// ("Login" or "Link") so the shared /Auth/Callback knows which side to land on.
        /// Trakt's OAuth is a standard code grant with a CSRF `state` — no PKCE. Returns
        /// null when Trakt credentials aren't configured (caller falls back to a redirect).
        /// </summary>
        private IActionResult BeginTraktOauth(string flow)
        {
            if (!_traktService.IsConfigured)
                return null;

            HttpContext.Session.SetString(OauthServiceKey, AnimeService.Trakt.ToString());
            HttpContext.Session.SetString(OauthFlowKey, flow);
            var state = BeginOauthState();
            return Redirect(_traktService.BuildAuthorizeUrl(state));
        }

        [HttpGet]
        public async Task<IActionResult> Callback(string code, string state = null)
        {
            // Read flow + provider before clearing — both AniList and MAL share this URL,
            // and the link-additional-provider flow lands here too.
            var oauthService = HttpContext.Session.GetString(OauthServiceKey);
            var oauthFlow = HttpContext.Session.GetString(OauthFlowKey);
            var malVerifier = HttpContext.Session.GetString(MalCodeVerifierKey);
            // Linked-account flow may carry a return URL (so /stremio-initiated links
            // come back to /stremio rather than always bouncing to /configure). Read
            // and clear in the same batch as the other OAuth-state keys.
            var oauthReturnUrl = HttpContext.Session.GetString(OauthReturnUrlKey);
            var expectedState = HttpContext.Session.GetString(OauthStateKey);
            var oauthNative = HttpContext.Session.GetString(OauthNativeKey) == "1";

            HttpContext.Session.Remove(OauthServiceKey);
            HttpContext.Session.Remove(OauthFlowKey);
            HttpContext.Session.Remove(MalCodeVerifierKey);
            HttpContext.Session.Remove(OauthReturnUrlKey);
            HttpContext.Session.Remove(OauthStateKey);
            HttpContext.Session.Remove(OauthNativeKey);

            // CSRF gate: the state we minted on the authorize redirect must round-trip
            // intact. A missing or mismatched value means this callback wasn't started by
            // this session — refuse before touching the authorization code so an attacker
            // can't graft their provider account onto a victim's session (account fixation).
            // In practice the legitimate way to land here is the back-button / refresh
            // landing on an already-consumed callback URL.
            //   - Authenticated user with no flow in flight: redirect home. "Sign-in link
            //     expired" would be confusing context for someone who's already signed in
            //     (they were typically chasing a bookmarked callback URL).
            //   - Otherwise: render the explicit OauthStateExpired view so the previously-
            //     anonymous user sees actionable "Sign in again" messaging instead of
            //     silently landing on the logged-out dashboard.
            if (string.IsNullOrEmpty(expectedState)
                || !string.Equals(expectedState, state, StringComparison.Ordinal))
            {
                _logger.LogWarning("OAuth callback rejected: state mismatch (service={Service}).", oauthService);
                if (GetSessionPrimary() != null)
                    return RedirectToReturnUrlOrHome(oauthReturnUrl);
                // No MVC views on the Blazor Web head — hand the previously-anonymous
                // user to the Blazor login page with a flag so it can show the
                // "sign-in link expired, try again" messaging (the OauthStateExpired
                // view's job in the old app).
                return Redirect("/login?expired=1");
            }

            var isLink = oauthFlow == OauthFlowLink;
            // Capture the primary's session token *before* the login path clears it. The
            // link path needs it to find the primary's UID and attach the linked token.
            var primarySession = HttpContext.Session.GetString("AccessToken");

            if (!isLink)
                HttpContext.Session.Remove("AccessToken");

            TokenData linkedTokenData = null;
            if (oauthService == AnimeService.MyAnimeList.ToString())
                linkedTokenData = await _tokenService.GetAccessTokenByMalCodeAsync(code, malVerifier, setSession: !isLink);
            else if (oauthService == AnimeService.Trakt.ToString())
            {
                // Trakt's OAuth exchange lives in TraktService; it returns a Trakt-tagged
                // TokenData. Mirror the other providers' setSession convention here (the
                // login flow seeds the session, the link flow leaves the primary's intact).
                linkedTokenData = await _traktService.ExchangeCodeAsync(code);
                if (!isLink && linkedTokenData != null)
                    HttpContext.Session.SetString("AccessToken", SerializeObject(linkedTokenData));
            }
            else
                linkedTokenData = await _tokenService.GetAccessTokenByCodeAsync(code, setSession: !isLink);

            if (isLink && linkedTokenData != null && !string.IsNullOrEmpty(primarySession))
                await PersistLinkedTokenAsync(primarySession, linkedTokenData);

            // Persist the primary + drop the auto-sign-in cookie. For the login flow
            // this is the freshly-exchanged primary; for the link flow it ensures the
            // already-logged-in primary has its cookie too (idempotent).
            await EnsurePrimaryPersistedAsync();

            // Native (MAUI) login: hand the result back to the app via its deep link with a
            // one-time code rather than the web localStorage bridge. The app exchanges the
            // code at /api/v1/auth/native/exchange for its config segment.
            if (oauthNative && !isLink)
            {
                var (_, nativeUid) = await _tokenService.ResolveCurrentAsync(_configStore);
                if (!string.IsNullOrEmpty(nativeUid))
                {
                    var oneTime = _nativeCodes.Issue(nativeUid);
                    return Redirect($"{NativeCallbackScheme}?code={Uri.EscapeDataString(oneTime)}");
                }
                return Redirect($"{NativeCallbackScheme}?error=login_failed");
            }

            // Both flows honour the stashed return URL when set. Link flow falls
            // back to /configure when nothing was stashed (the link partial always
            // sets it, but be defensive); login flow falls back to the home
            // dashboard so anonymous-to-authenticated transitions land somewhere
            // generic by default unless the caller picked a specific destination.
            return isLink
                ? RedirectToReturnUrlOrConfigure(oauthReturnUrl)
                : RedirectToReturnUrlOrHome(oauthReturnUrl);
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
        /// Persists the just-logged-in primary into the config store and drops the
        /// long-lived <c>anisync_uid</c> cookie, so the session can be rehydrated
        /// after the in-memory session store is dropped (Fly machine restart /
        /// redeploy, or a PWA reopened after the 30-day session window). Without
        /// this the cookie + row were only minted on the first /configure or
        /// /account visit, so a user who just signed in and browsed the web app had
        /// nothing to auto-sign-in from after a restart.
        /// <para>
        /// Mirrors <see cref="HomeController"/>'s configure-page resolution: dedupe
        /// to a stable UID via the identity index (idempotent across re-logins), and
        /// when the user signed in through a service that's a linked secondary on an
        /// existing row, refresh that link and route the session to the row's primary
        /// rather than forking a duplicate account. No-op for anonymous sessions.
        /// </para>
        /// </summary>
        private async Task EnsurePrimaryPersistedAsync()
        {
            var primary = GetSessionPrimary();
            if (primary == null) return; // null also covers anonymous sessions

            string uid;
            var (matchedUid, isPrimaryMatch) = await _configStore.FindUidByIdentityAsync(primary);
            if (!string.IsNullOrEmpty(matchedUid))
            {
                uid = matchedUid;
                if (isPrimaryMatch)
                {
                    await _configStore.UpdateByUserAsync(primary);
                }
                else
                {
                    await _configStore.SetLinkedTokenAsync(uid, new LinkedToken
                    {
                        Service = primary.anime_service,
                        TokenData = primary,
                        NeedsReauth = false,
                    });
                    var existingPrimary = await _configStore.GetAsync(uid);
                    if (existingPrimary != null)
                        HttpContext.Session.SetString("AccessToken", SerializeObject(existingPrimary));
                }
            }
            else
            {
                uid = await _configStore.UpsertAsync(primary);
            }

            _tokenService.SetPrimaryUidCookie(uid);
        }

        /// <summary>
        /// Starts an OAuth flow that will store the resulting token as a linked provider
        /// rather than the primary login. Requires the user to already be logged in.
        /// </summary>
        [HttpGet]
        public IActionResult LinkProvider(AnimeService service, string returnUrl = null)
        {
            var primary = GetSessionPrimary();
            if (primary == null) return BadRequest("Log in with a primary provider first.");
            if (primary.anime_service == service) return BadRequest("This provider is already your primary account.");

            // Stash the AniSync page that started the link so /Auth/Callback can
            // hand the user back to it after the OAuth round-trip. Validated as a
            // local URL here so a malformed / hostile value never even makes it
            // into the session — the Callback can trust whatever it reads.
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                HttpContext.Session.SetString(OauthReturnUrlKey, returnUrl);
            }
            else
            {
                HttpContext.Session.Remove(OauthReturnUrlKey);
            }

            if (service == AnimeService.Anilist)
            {
                HttpContext.Session.SetString(OauthServiceKey, AnimeService.Anilist.ToString());
                HttpContext.Session.SetString(OauthFlowKey, OauthFlowLink);
                var clientId = _configuration["Anilist:ClientId"];
                var state = BeginOauthState();
                return Redirect($"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=code&state={Uri.EscapeDataString(state)}");
            }

            if (service == AnimeService.MyAnimeList)
            {
                return BeginMalOauth(OauthFlowLink);
            }

            if (service == AnimeService.Trakt)
            {
                return BeginTraktOauth(OauthFlowLink)
                    ?? BadRequest("Trakt client is not configured. Set Trakt:ClientId / ClientSecret / RedirectUri.");
            }

            // Kitsu uses password grant — UI posts to /Auth/LinkKitsu instead.
            return BadRequest("Use /Auth/LinkKitsu for Kitsu.");
        }

        /// <summary>
        /// Links a Kitsu account using the username/password grant Kitsu requires. Posted
        /// from the inline form on the configure page.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> LinkKitsu(string username, string password, string returnUrl = null)
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

            return RedirectToReturnUrlOrConfigure(returnUrl);
        }

        /// <summary>
        /// Promotes a linked provider to primary on the current install. UID is preserved so
        /// existing Stremio installs keep working — only the primary slot rotates. POST to
        /// avoid CSRF-style triggers via image tags or prefetch.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SetPrimary(AnimeService service, bool force = false, string returnUrl = null)
        {
            var primary = GetSessionPrimary();
            if (primary == null) return BadRequest("Log in with a primary provider first.");
            if (primary.anime_service == service) return BadRequest("This provider is already your primary account.");

            var uid = await _configStore.UpsertAsync(primary);
            var (newPrimary, reason) = await _configStore.SwapPrimaryAsync(uid, service, resolveCollision: force);
            if (newPrimary == null)
                return BadRequest(BuildSwapErrorMessage(reason, service));

            // Push the new primary into the session so subsequent requests dispatch through
            // its service. The UID stayed the same, so install URLs continue to resolve.
            HttpContext.Session.SetString("AccessToken", SerializeObject(newPrimary));
            return RedirectToReturnUrlOrConfigure(returnUrl);
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
        public async Task<IActionResult> UnlinkProvider(AnimeService service, string returnUrl = null)
        {
            var primary = GetSessionPrimary();
            if (primary == null) return BadRequest("Log in with a primary provider first.");

            var uid = await _configStore.UpsertAsync(primary);
            await _configStore.RemoveLinkedTokenAsync(uid, service);
            return RedirectToReturnUrlOrConfigure(returnUrl);
        }

        /// <summary>
        /// Returns the primary's full library so the client can drive the backfill loop.
        /// One round-trip — the server holds no per-job state. Cancellation, batching,
        /// and progress live entirely on the configure page in JS.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SyncEntries()
        {
            var primary = GetSessionPrimary();
            if (primary == null) return BadRequest("Log in with a primary provider first.");

            var primaryEntries = await _syncService.GetPrimaryEntriesAsync(primary);

            // Pre-compute which entries actually need writing. If a secondary already has
            // the same status + progress for an entry, fan-out is a no-op — skip it. This
            // turns "write 247 entries × N secondaries" into "write the diff", which for a
            // mostly-aligned library means single-digit writes instead of hundreds.
            var idsToSync = await ComputeOutOfSyncIdsAsync(primary, primaryEntries);
            var filtered = idsToSync == null
                ? primaryEntries
                : primaryEntries.Where(e => idsToSync.Contains(e.MediaId)).ToList();

            return Ok(new
            {
                total = filtered.Count,
                primaryTotal = primaryEntries.Count,
                // Lowercase shape so the JS contract doesn't depend on the serializer's
                // default casing — System.Text.Json's camelCase is on by default in MVC,
                // but explicit > implicit when the client poller is going to depend on it.
                entries = filtered.Select(e => new
                {
                    mediaId = e.MediaId,
                    status = e.Status,
                    progress = e.Progress,
                    score = e.Score,
                    notes = e.Notes,
                    rewatchCount = e.RewatchCount,
                    startedAt = e.StartedAt,
                    finishedAt = e.FinishedAt,
                }).ToList(),
            });
        }

        /// <summary>
        /// Builds the union of out-of-sync media ids across every linked secondary so the
        /// caller can write only the entries that actually differ. An entry is "out of
        /// sync" if a secondary either has no record of it, or has a different normalised
        /// status / progress. Returns null when we couldn't safely diff (no UID, no
        /// healthy secondaries) — callers fall back to syncing everything.
        /// </summary>
        private async Task<HashSet<string>> ComputeOutOfSyncIdsAsync(TokenData primary, List<AnimeEntry> primaryEntries)
        {
            if (primary == null || primary.anonymousUser) return null;

            var uid = await _configStore.UpsertAsync(primary);
            if (string.IsNullOrEmpty(uid)) return null;

            var linked = await _configStore.GetLinkedTokensAsync(uid);
            if (linked.Count == 0) return new HashSet<string>(); // Nothing linked → nothing to sync.

            var idsToSync = new HashSet<string>();

            foreach (var l in linked)
            {
                if (l.NeedsReauth || l.TokenData == null) continue;

                List<AnimeEntry> secondary;
                try
                {
                    secondary = l.Service switch
                    {
                        AnimeService.Anilist => await _anilistService.GetUserListEntriesAsync(l.TokenData),
                        AnimeService.Kitsu => await _kitsuService.GetUserListEntriesAsync(l.TokenData),
                        AnimeService.MyAnimeList => await _malService.GetUserListEntriesAsync(l.TokenData),
                        _ => [],
                    };
                }
                catch (Exception ex)
                {
                    // Couldn't read this secondary — fall back to "sync everything to it".
                    // FanOutSaveAsync writes to all linked targets anyway, so adding every
                    // primary id to the set just makes the dead secondary's writes attempted
                    // (it'll fail per-entry the same way it would in the original full-sync).
                    _logger.LogWarning(ex, "[SyncEntries] couldn't read {Service} library — including every primary id in the sync set.", l.Service);
                    foreach (var p in primaryEntries)
                        if (!string.IsNullOrEmpty(p.MediaId))
                            idsToSync.Add(p.MediaId);
                    continue;
                }

                var prefix = GetServicePrefix(l.Service);

                var byId = secondary
                    .Where(e => !string.IsNullOrEmpty(e.MediaId))
                    .ToDictionary(e => e.MediaId, e => e);

                foreach (var p in primaryEntries)
                {
                    if (string.IsNullOrEmpty(p.MediaId)) continue;
                    if (idsToSync.Contains(p.MediaId)) continue;

                    var resolved = await _mappingService.GetIdByService(p.MediaId, l.Service);
                    if (string.IsNullOrEmpty(resolved)) continue;

                    var key = $"{prefix}{resolved}";
                    if (!byId.TryGetValue(key, out var s))
                    {
                        idsToSync.Add(p.MediaId); // Missing on this secondary.
                        continue;
                    }

                    var pStatus = NormalizeListStatus(p.Status);
                    var sStatus = NormalizeListStatus(s.Status);
                    if (pStatus != sStatus || p.Progress != s.Progress)
                        idsToSync.Add(p.MediaId);
                }
            }

            return idsToSync;
        }

        public class SyncBatchRequest
        {
            public List<SyncBatchEntry> Entries { get; set; }
        }

        public class SyncBatchEntry
        {
            public string MediaId { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public double? Score { get; set; }
            public string Notes { get; set; }
            public int? RewatchCount { get; set; }
            public DateTime? StartedAt { get; set; }
            public DateTime? FinishedAt { get; set; }
        }

        /// <summary>
        /// Fans out a batch of primary-library entries through the existing sync path. The
        /// client picks the batch size (typically a handful of entries per request so the
        /// progress bar moves in visible increments and Cancel is responsive). Returns
        /// per-batch counts; the client accumulates the running total locally.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SyncBatch([FromBody] SyncBatchRequest request)
        {
            var primary = GetSessionPrimary();
            if (primary == null) return BadRequest("Log in with a primary provider first.");

            int completed = 0, failed = 0;
            foreach (var entry in request?.Entries ?? new())
            {
                if (string.IsNullOrEmpty(entry?.MediaId))
                {
                    failed++;
                    continue;
                }

                try
                {
                    await _syncService.FanOutSaveAsync(primary, entry.MediaId, season: null,
                        entry.Progress, entry.Status, entry.Score, entry.Notes,
                        entry.RewatchCount, entry.StartedAt, entry.FinishedAt);
                    completed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SyncBatch entry {MediaId} failed.", entry.MediaId);
                    failed++;
                }
            }

            return Ok(new { completed, failed });
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

        /// <summary>
        /// Renders the standalone signup form. Kitsu is the only one of the three providers
        /// we support that exposes a public registration API (JSON:API users endpoint) —
        /// AniList and MAL require a manual signup on their own sites.
        /// Authenticated users are bounced to home: creating another account from a logged-
        /// in session would mint a second config row that the current cookie doesn't own,
        /// leaving the user staring at a signup form they shouldn't be on.
        /// </summary>
        [HttpGet]
        public IActionResult Register(string returnUrl = null)
        {
            if (GetSessionPrimary() != null) return RedirectToReturnUrlOrHome(returnUrl);
            // The signup form is a Blazor page (/register) on this head — it POSTs
            // back to this action. A bare GET just lands the user on it, preserving
            // the post-signup destination.
            var q = (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                ? $"?returnUrl={Uri.EscapeDataString(returnUrl)}" : "";
            return Redirect("/register" + q);
        }

        /// <summary>
        /// Bounces a failed signup back to the Blazor <c>/register</c> page with the
        /// error + prefilled fields in the query string (the old app re-rendered the
        /// View with ViewBag; this head has no MVC views).
        /// </summary>
        private IActionResult RegisterErrorRedirect(string error, string name, string email, string returnUrl)
        {
            var q = new List<string> { $"error={Uri.EscapeDataString(error ?? "Signup failed.")}" };
            if (!string.IsNullOrEmpty(name)) q.Add($"name={Uri.EscapeDataString(name)}");
            if (!string.IsNullOrEmpty(email)) q.Add($"email={Uri.EscapeDataString(email)}");
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                q.Add($"returnUrl={Uri.EscapeDataString(returnUrl)}");
            return Redirect("/register?" + string.Join("&", q));
        }

        [HttpPost]
        public async Task<IActionResult> Register(string name, string email, string password, string confirmPassword, string returnUrl = null)
        {
            // Defence-in-depth against the GET guard above — a stale tab that started the
            // form while logged out shouldn't be able to post once the user has signed in
            // somewhere else in the meantime.
            if (GetSessionPrimary() != null) return RedirectToReturnUrlOrHome(returnUrl);

            // Mirror the basic constraints Kitsu would reject server-side anyway, but
            // surface them inline so the user doesn't pay a network round-trip to learn
            // their password is too short or doesn't match the confirmation.
            string error = null;
            if (string.IsNullOrWhiteSpace(name)) error = "Username is required.";
            else if (string.IsNullOrWhiteSpace(email)) error = "Email is required.";
            else if (string.IsNullOrWhiteSpace(password)) error = "Password is required.";
            else if (password.Length < 8) error = "Password must be at least 8 characters.";
            else if (password != confirmPassword) error = "Passwords don't match.";

            if (error != null)
                return RegisterErrorRedirect(error, name, email, returnUrl);

            // Atomic (IP, email) lock for the Kitsu signup -> password-grant pair. The
            // submit button has a JS disable too, but a second tab on the same page (or a
            // misconfigured browser-extension form-filler) can still fire two concurrent
            // POSTs that both pass Kitsu's duplicate-email check before either lands.
            // TryAdd is the cheap atomic claim; the slot drops in `finally` so a real
            // signup failure doesn't block the user's retry.
            var lockKey = RegisterLockKey(HttpContext, email);
            if (!_registerInFlight.TryAdd(lockKey, 0))
                return RegisterErrorRedirect(
                    "A signup is already in progress for this email. Wait a moment and try again.",
                    name, email, returnUrl);

            try
            {
                var (ok, kitsuError) = await _kitsuService.RegisterAsync(name, email, password);
                if (!ok)
                    return RegisterErrorRedirect(kitsuError, name, email, returnUrl);

                // Account exists — drop straight into the same password-grant flow Login uses
                // so the user lands authenticated. Kitsu's OAuth password grant only resolves
                // identities by email, not by the freshly-created username, so the email is
                // what we hand it. returnUrl lets the caller pick the post-signup destination
                // (defaults to the home dashboard) — Stremio-initiated signups land on
                // /configure, identity-page signups land on home.
                await _tokenService.GetAccessTokenByCredsAsync(email, password, setContext: true);
                await EnsurePrimaryPersistedAsync();
                return RedirectToReturnUrlOrHome(returnUrl);
            }
            finally
            {
                _registerInFlight.TryRemove(lockKey, out _);
            }
        }

        private static string RegisterLockKey(HttpContext ctx, string email)
        {
            // Fly-Client-IP is set + overwritten by Fly's edge (can't be spoofed); fall
            // back to the connection peer for local dev / non-Fly hosting. Email is
            // lowercased so Foo@bar.com and foo@bar.com share a lock slot the way Kitsu's
            // duplicate-email check does.
            var fly = ctx.Request.Headers["Fly-Client-IP"].ToString();
            var ip = !string.IsNullOrWhiteSpace(fly)
                ? fly.Trim()
                : ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
            return $"{ip}|{(email ?? string.Empty).Trim().ToLowerInvariant()}";
        }

        public async Task<IActionResult> Logout(string returnUrl = null)
        {
            // Speculation guard. Logout is a state-mutating GET, so any
            // speculative fetch of this URL — a browser's built-in link
            // prefetch / address-bar preload, a crawler, a prefetching
            // extension — would silently disconnect the user before they ever
            // clicked. Browsers stamp speculative navigations with
            // `Sec-Purpose: prefetch[;prerender]`; if present, bounce to the
            // dashboard WITHOUT clearing the session. The real click carries no
            // such header and proceeds normally. (The app no longer ships its
            // own Speculation Rules, but this backstop stays — it guards against
            // every prefetch source, not just our own.)
            var secPurpose = Request.Headers["Sec-Purpose"].ToString();
            if (!string.IsNullOrEmpty(secPurpose) &&
                secPurpose.Contains("prefetch", StringComparison.OrdinalIgnoreCase))
            {
                // Didn't clear the session — go straight home without the seeding
                // bridge (no credential change to propagate to the client).
                return Redirect("/");
            }

            // Pure disconnect: clears the session cookie + in-memory token cache so the
            // user lands on the dashboard as an anonymous visitor, but leaves the config
            // row in place. Logging back in with the same identity restores the original
            // install URL, linked accounts, scrobble token, and flag bits intact — same
            // UID, same row. The "Delete Configuration" Danger Zone action is the
            // destructive sibling for users who actually want their data gone.
            await _tokenService.RemoveCachedUser();

            // Disconnect-return whitelist: only /account and /configure are valid
            // post-logout landing pads. Any other source (drawer link clicked from
            // an anime detail page, the watch page, library, etc.) falls back to
            // the home dashboard — landing the user back on a deep page after
            // they've explicitly disconnected reads as confusing rather than
            // helpful. Url.IsLocalUrl already screens against external redirects;
            // the path-level whitelist enforces the user's intent on top of that.
            // Route the post-logout landing through the seeding bridge so the thin
            // client's stored X-AniSync-Config credential is cleared from localStorage
            // (the session is gone, so the bridge resolves no UID and removes it).
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                var path = returnUrl.Split('?', 2)[0].TrimEnd('/');
                if (string.Equals(path, "/account", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(path, "/configure", StringComparison.OrdinalIgnoreCase))
                {
                    return SeedConfigAndRedirect(returnUrl);
                }
            }
            return SeedConfigAndRedirect("/");
        }
    }
}