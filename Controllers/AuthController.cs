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
        private readonly ISyncService _syncService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly IAnimeMappingService _mappingService;
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

        /// <summary>
        /// Returns a redirect to <paramref name="returnUrl"/> when it's a safe same-app
        /// local URL, otherwise the standard /Home/Configure redirect. Every linked-
        /// account action takes a returnUrl so the user lands back on whichever page
        /// (/configure or /stremio) they triggered the action from — the partials are
        /// rendered on both surfaces and would otherwise always bounce to /configure.
        /// </summary>
        private IActionResult RedirectToReturnUrlOrConfigure(string returnUrl)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            return RedirectToAction("Configure", "Home");
        }

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
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            return RedirectToAction("Index", "Home");
        }

        public AuthController(ITokenService tokenService, IHttpContextAccessor httpContextAccessor,
            IConfigStore configStore, IConfiguration configuration, ISyncService syncService,
            IAnilistService anilistService, IKitsuService kitsuService, IMalService malService,
            IAnimeMappingService mappingService, ILogger<AuthController> logger)
        {
            _tokenService = tokenService;
            _httpContextAccessor = httpContextAccessor;
            _configStore = configStore;
            _configuration = configuration;
            _syncService = syncService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _mappingService = mappingService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Login(AnimeService? animeService = null, string username = null, string password = null, bool anonymous = false, string returnUrl = null)
        {
            // Stash the post-login destination on the session for the OAuth
            // branches (AniList / MAL) so /Auth/Callback can read + honour
            // it on return. Same key the link-additional-provider flow uses;
            // Callback always reads-and-clears it regardless of which flow
            // populated it.
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                HttpContext.Session.SetString(OauthReturnUrlKey, returnUrl);
            else
                HttpContext.Session.Remove(OauthReturnUrlKey);

            if (anonymous)
            {
                var tokenData = new TokenData
                {
                    anime_service = animeService ?? AnimeService.Kitsu
                };

                _httpContextAccessor.HttpContext.Session.SetString("AccessToken", SerializeObject(tokenData));

                return RedirectToReturnUrlOrHome(returnUrl);
            }
            else if (animeService == AnimeService.Anilist)
            {
                HttpContext.Session.SetString(OauthServiceKey, AnimeService.Anilist.ToString());
                HttpContext.Session.SetString(OauthFlowKey, OauthFlowLogin);
                var clientId = _configuration["Anilist:ClientId"];
                var state = BeginOauthState();
                return Redirect($"https://anilist.co/api/v2/oauth/authorize?client_id={clientId}&response_type=code&state={Uri.EscapeDataString(state)}");
            }
            else if (animeService == AnimeService.MyAnimeList)
            {
                return BeginMalOauth(OauthFlowLogin) ?? RedirectToReturnUrlOrHome(returnUrl);
            }
            else
            {
                await _tokenService.GetAccessTokenByCredsAsync(username, password, true);

                return RedirectToReturnUrlOrHome(returnUrl);
            }
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

            HttpContext.Session.Remove(OauthServiceKey);
            HttpContext.Session.Remove(OauthFlowKey);
            HttpContext.Session.Remove(MalCodeVerifierKey);
            HttpContext.Session.Remove(OauthReturnUrlKey);
            HttpContext.Session.Remove(OauthStateKey);

            // CSRF gate: the state we minted on the authorize redirect must round-trip
            // intact. A missing or mismatched value means this callback wasn't started by
            // this session — refuse before touching the authorization code so an attacker
            // can't graft their provider account onto a victim's session (account fixation).
            if (string.IsNullOrEmpty(expectedState)
                || !string.Equals(expectedState, state, StringComparison.Ordinal))
            {
                _logger.LogWarning("OAuth callback rejected: state mismatch (service={Service}).", oauthService);
                return RedirectToAction("Index", "Home");
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
            else
                linkedTokenData = await _tokenService.GetAccessTokenByCodeAsync(code, setSession: !isLink);

            if (isLink && linkedTokenData != null && !string.IsNullOrEmpty(primarySession))
                await PersistLinkedTokenAsync(primarySession, linkedTokenData);

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
        /// </summary>
        [HttpGet]
        public IActionResult Register(string returnUrl = null)
        {
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Register(string name, string email, string password, string confirmPassword, string returnUrl = null)
        {
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
            {
                ViewBag.Error = error;
                ViewBag.Name = name;
                ViewBag.Email = email;
                ViewBag.ReturnUrl = returnUrl;
                return View();
            }

            var (ok, kitsuError) = await _kitsuService.RegisterAsync(name, email, password);
            if (!ok)
            {
                ViewBag.Error = kitsuError;
                ViewBag.Name = name;
                ViewBag.Email = email;
                ViewBag.ReturnUrl = returnUrl;
                return View();
            }

            // Account exists — drop straight into the same password-grant flow Login uses
            // so the user lands authenticated. Kitsu's OAuth password grant only resolves
            // identities by email, not by the freshly-created username, so the email is
            // what we hand it. returnUrl lets the caller pick the post-signup destination
            // (defaults to the home dashboard) — Stremio-initiated signups land on
            // /configure, identity-page signups land on home.
            await _tokenService.GetAccessTokenByCredsAsync(email, password, setContext: true);
            return RedirectToReturnUrlOrHome(returnUrl);
        }

        public async Task<IActionResult> Logout(string returnUrl = null)
        {
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
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                var path = returnUrl.Split('?', 2)[0].TrimEnd('/');
                if (string.Equals(path, "/account", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(path, "/configure", StringComparison.OrdinalIgnoreCase))
                {
                    return Redirect(returnUrl);
                }
            }
            return RedirectToAction("Index", "Home");
        }
    }
}