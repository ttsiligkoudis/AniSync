using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;

namespace AnimeList.Services
{
    public class TokenService : ITokenService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfigStore _configStore;
        private readonly IConfiguration _configuration;
        // Trakt's OAuth exchange/refresh lives in TraktService (it owns the Trakt API
        // surface). TokenService delegates the Trakt refresh here so the primary +
        // linked refresh paths stay uniform. Safe (no DI cycle): TraktService depends on
        // IConfigStore / IConfiguration / IHttpClientFactory, never on ITokenService.
        private readonly ITraktService _traktService;
        // In-process token caches keyed by user_id (AniList/MAL) or username (Kitsu). Backed by
        // MemoryCache rather than a plain dictionary so they stay bounded: a 30-minute sliding
        // window keeps only recently-active users (an AniList token lives ~1 year, so a
        // dictionary would have pinned every user ever seen in memory for that whole time), and
        // SizeLimit is a hard ceiling. Reads re-check IsTokenExpired and fall back to the
        // store/refresh path on a miss, so eviction is always safe.
        private static readonly MemoryCacheEntryOptions _tokenCacheEntry =
            new() { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) };
        private static readonly MemoryCache _kitsuTokenCache = new(new MemoryCacheOptions { SizeLimit = 20_000 });
        private static readonly MemoryCache _anilistTokenCache = new(new MemoryCacheOptions { SizeLimit = 20_000 });
        private static readonly MemoryCache _malTokenCache = new(new MemoryCacheOptions { SizeLimit = 20_000 });
        private static readonly MemoryCache _traktTokenCache = new(new MemoryCacheOptions { SizeLimit = 20_000 });

        // Per-(service, cache-key) refresh lock. AniList and MAL rotate the
        // refresh_token on every successful exchange — two concurrent callers
        // refreshing the same expired token would both submit the same
        // refresh_token, only the first succeeds, and the second comes back
        // with an invalid_grant that surfaces to the user as "logged out".
        // The semaphore serialises the refresh; after acquiring it, callers
        // re-check the cache so the second one reuses the first's result
        // instead of doing a redundant network round-trip.
        // The dictionary accumulates entries forever, but cardinality is
        // bounded by the number of unique users (one SemaphoreSlim each is
        // tiny) so we don't bother evicting.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();

        private static SemaphoreSlim LockFor(string key) =>
            _refreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        public TokenService(IHttpClientFactory clientFactory, IHttpContextAccessor httpContextAccessor, IConfigStore configStore, IConfiguration configuration, ITraktService traktService)
        {
            _clientFactory = clientFactory;
            _httpContextAccessor = httpContextAccessor;
            _configStore = configStore;
            _configuration = configuration;
            _traktService = traktService;
        }

        // Pulled from configuration so deployments can supply their own MyAnimeList app
        // credentials without rebuilding. The redirect URI must match what's registered
        // on the MAL developer dashboard for that client id.
        private string MalClientId => _configuration["Mal:ClientId"];
        private string MalClientSecret => _configuration["Mal:ClientSecret"];
        private string MalRedirectUri => _configuration["Mal:RedirectUri"];

        // AniList OAuth client config. Moved out of source (it used to be compiled-in
        // constants in Utils) so neither the client_id nor the confidential client_secret
        // is committed — supply both via Anilist__ClientId / Anilist__ClientSecret as Fly
        // secrets / env vars. Only RedirectUri ships as a committed default. Coalesced to
        // empty so a missing value degrades to a failed exchange rather than a null arg.
        private string AnilistClientId => _configuration["Anilist:ClientId"] ?? string.Empty;
        private string AnilistClientSecret => _configuration["Anilist:ClientSecret"] ?? string.Empty;
        private string AnilistRedirectUri => _configuration["Anilist:RedirectUri"] ?? string.Empty;

        public async Task<TokenData> GetAccessTokenAsync(string config = null)
        {
            TokenData tokenData = null;

            if (!string.IsNullOrEmpty(config))
            {
                var configuration = DecodeConfig(config);

                if (!string.IsNullOrEmpty(configuration?.tokenUid))
                {
                    // v5: token JSON lives in the config store, looked up by UID.
                    tokenData = await _configStore.GetAsync(configuration.tokenUid);
                }
                else if (!string.IsNullOrEmpty(configuration?.tokenData))
                {
                    // v3: anonymous install with token JSON inline in the URL.
                    tokenData = DeserializeObject<TokenData>(configuration.tokenData);
                }
            }
            else
            {
                var sessionStr = _httpContextAccessor.HttpContext.Session.GetString("AccessToken");
                if (!string.IsNullOrEmpty(sessionStr))
                {
                    tokenData = DeserializeObject<TokenData>(sessionStr);
                }
                else
                {
                    // Session miss — likely a fly.io redeploy dropped the in-memory
                    // session store, or the user reopened the installed PWA after a
                    // long pause. Rehydrate from the persistent UID cookie if one
                    // is present: GetAsync(uid) reads the per-user row in the
                    // SQLite config store and gives us back the token data we
                    // last wrote on login. The session is then re-seeded so the
                    // rest of the request lifecycle works normally.
                    var uidCookie = _httpContextAccessor.HttpContext.Request.Cookies[UidCookieName];
                    if (!string.IsNullOrEmpty(uidCookie))
                    {
                        tokenData = await _configStore.GetAsync(uidCookie);
                        if (tokenData != null)
                        {
                            _httpContextAccessor.HttpContext.Session.SetString(
                                "AccessToken", SerializeObject(tokenData));
                        }
                    }
                }
            }

            if (tokenData == null) return null;

            if (tokenData.anonymousUser)
            {
                return tokenData;
            }

            if (tokenData.anime_service == AnimeService.Anilist)
            {
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return null;

                var cacheKey = tokenData.user_id;
                if (!string.IsNullOrEmpty(cacheKey)
                    && _anilistTokenCache.TryGetValue(cacheKey, out TokenData cached)
                    && !IsTokenExpired(cached.expiration_date))
                {
                    tokenData = cached;
                }
                else if (IsTokenExpired(tokenData.expiration_date) && !string.IsNullOrEmpty(tokenData.refresh_token))
                {
                    // AniList rotates the refresh_token on every exchange; concurrent
                    // refreshes for the same user would race and the loser comes back
                    // logged out. Serialise per user (see _refreshLocks above), and
                    // re-check the cache after acquiring so the second caller reuses
                    // the first's freshly-rotated token.
                    var sem = LockFor("anilist:" + (cacheKey ?? string.Empty));
                    await sem.WaitAsync();
                    try
                    {
                        if (!string.IsNullOrEmpty(cacheKey)
                            && _anilistTokenCache.TryGetValue(cacheKey, out TokenData freshly)
                            && !IsTokenExpired(freshly.expiration_date))
                        {
                            tokenData = freshly;
                        }
                        else
                        {
                            var refreshed = await RefreshAccessToken(tokenData.refresh_token);
                            if (refreshed != null && !string.IsNullOrEmpty(cacheKey))
                            {
                                _anilistTokenCache.Set(cacheKey, refreshed, _tokenCacheEntry);
                                // Write back to the config store so v5 install URLs survive token rotation.
                                await _configStore.UpdateByUserAsync(refreshed);
                            }
                            tokenData = refreshed;
                        }
                    }
                    finally
                    {
                        sem.Release();
                    }
                }
            }
            else if (tokenData.anime_service == AnimeService.MyAnimeList)
            {
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return null;

                var cacheKey = tokenData.user_id;
                if (!string.IsNullOrEmpty(cacheKey)
                    && _malTokenCache.TryGetValue(cacheKey, out TokenData cached)
                    && !IsTokenExpired(cached.expiration_date))
                {
                    tokenData = cached;
                }
                else if (IsTokenExpired(tokenData.expiration_date) && !string.IsNullOrEmpty(tokenData.refresh_token))
                {
                    // Same rotated-refresh_token race as AniList — MAL also issues a
                    // fresh refresh_token on each exchange.
                    var sem = LockFor("mal:" + (cacheKey ?? string.Empty));
                    await sem.WaitAsync();
                    try
                    {
                        if (!string.IsNullOrEmpty(cacheKey)
                            && _malTokenCache.TryGetValue(cacheKey, out TokenData freshly)
                            && !IsTokenExpired(freshly.expiration_date))
                        {
                            tokenData = freshly;
                        }
                        else
                        {
                            var refreshed = await RefreshMalAccessToken(tokenData.refresh_token);
                            if (refreshed != null && !string.IsNullOrEmpty(cacheKey))
                            {
                                // Preserve identity fields the refresh response doesn't echo back
                                refreshed.user_id ??= tokenData.user_id;
                                refreshed.username ??= tokenData.username;
                                _malTokenCache.Set(cacheKey, refreshed, _tokenCacheEntry);
                                await _configStore.UpdateByUserAsync(refreshed);
                            }
                            tokenData = refreshed;
                        }
                    }
                    finally
                    {
                        sem.Release();
                    }
                }
            }
            else if (tokenData.anime_service == AnimeService.Trakt)
            {
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return null;

                var cacheKey = tokenData.user_id;
                if (!string.IsNullOrEmpty(cacheKey)
                    && _traktTokenCache.TryGetValue(cacheKey, out TokenData cached)
                    && !IsTokenExpired(cached.expiration_date))
                {
                    tokenData = cached;
                }
                else if (IsTokenExpired(tokenData.expiration_date) && !string.IsNullOrEmpty(tokenData.refresh_token))
                {
                    // Trakt rotates the refresh_token on every exchange, same as AniList/MAL —
                    // serialise per user so concurrent refreshes don't race each other into an
                    // invalid_grant. The refresh itself is delegated to TraktService.
                    var sem = LockFor("trakt:" + (cacheKey ?? string.Empty));
                    await sem.WaitAsync();
                    try
                    {
                        if (!string.IsNullOrEmpty(cacheKey)
                            && _traktTokenCache.TryGetValue(cacheKey, out TokenData freshly)
                            && !IsTokenExpired(freshly.expiration_date))
                        {
                            tokenData = freshly;
                        }
                        else
                        {
                            var refreshed = await _traktService.RefreshTokenAsync(tokenData);
                            if (refreshed != null && !string.IsNullOrEmpty(cacheKey))
                            {
                                _traktTokenCache.Set(cacheKey, refreshed, _tokenCacheEntry);
                                await _configStore.UpdateByUserAsync(refreshed);
                            }
                            tokenData = refreshed;
                        }
                    }
                    finally
                    {
                        sem.Release();
                    }
                }
            }
            else
            {
                var cacheKey = tokenData.username;
                if (!string.IsNullOrEmpty(cacheKey)
                    && _kitsuTokenCache.TryGetValue(cacheKey, out TokenData cached)
                    && !IsTokenExpired(cached.expiration_date))
                {
                    tokenData = cached;
                }
                else if (IsTokenExpired(tokenData.expiration_date) && !string.IsNullOrEmpty(tokenData.refresh_token))
                {
                    // Only refresh when the stored token is actually expired — mirrors the
                    // AniList / MAL branches above. The previous code refreshed on every cache
                    // miss (cold start, redeploy, eviction of the in-process cache), so a
                    // transient Kitsu 5xx / rate-limit made RefreshKitsuAccessToken return null
                    // and logged the user out despite holding a still-valid token. Uses the
                    // refresh_token Kitsu issued during the initial password grant.
                    // Kitsu (Doorkeeper) also invalidates the prior refresh_token on a
                    // successful exchange, so the same per-user serialisation applies.
                    var sem = LockFor("kitsu:" + (cacheKey ?? string.Empty));
                    await sem.WaitAsync();
                    try
                    {
                        if (!string.IsNullOrEmpty(cacheKey)
                            && _kitsuTokenCache.TryGetValue(cacheKey, out TokenData freshly)
                            && !IsTokenExpired(freshly.expiration_date))
                        {
                            tokenData = freshly;
                        }
                        else
                        {
                            var refreshed = await RefreshKitsuAccessToken(tokenData.refresh_token, tokenData.username, tokenData.user_id);
                            if (refreshed != null && !string.IsNullOrEmpty(cacheKey))
                            {
                                _kitsuTokenCache.Set(cacheKey, refreshed, _tokenCacheEntry);
                                await _configStore.UpdateByUserAsync(refreshed);
                            }
                            tokenData = refreshed;
                        }
                    }
                    finally
                    {
                        sem.Release();
                    }
                }
                // else: cache miss but the stored token is still valid — use it as-is, no
                // refresh round-trip (and no risk of nulling out a good token).
            }

            return tokenData?.Clone();
        }

        public async Task RemoveCachedUser()
        {
            var tokenData = await GetAccessTokenAsync();

            if (tokenData == null) return;

            if (tokenData.anime_service == AnimeService.Anilist && !string.IsNullOrEmpty(tokenData.user_id))
            {
                _anilistTokenCache.Remove(tokenData.user_id);
            }
            else if (tokenData.anime_service == AnimeService.MyAnimeList && !string.IsNullOrEmpty(tokenData.user_id))
            {
                _malTokenCache.Remove(tokenData.user_id);
            }
            else if (tokenData.anime_service == AnimeService.Trakt && !string.IsNullOrEmpty(tokenData.user_id))
            {
                _traktTokenCache.Remove(tokenData.user_id);
            }
            else if (tokenData.anime_service == AnimeService.Kitsu && !string.IsNullOrEmpty(tokenData.username))
            {
                _kitsuTokenCache.Remove(tokenData.username);
            }

            _httpContextAccessor.HttpContext.Session.Remove("AccessToken");
            ClearPrimaryUidCookie();
        }

        // Persistent UID cookie. Lives across PWA / browser restarts so the
        // session can be rehydrated from the SQLite config store in
        // GetAccessTokenAsync above. 1-year MaxAge is plenty for the
        // "stays logged in" UX users expect from a tracker app; the
        // upstream OAuth tokens themselves (AniList ~1y, MAL 31d w/
        // refresh, Kitsu password-grant w/ refresh) handle their own
        // renewal cycles independently.
        public const string UidCookieName = "anisync_uid";

        /// <summary>
        /// Cookie → session rehydration without the token-refresh side
        /// effects of <see cref="GetAccessTokenAsync"/>. Used by the request
        /// pipeline middleware so every request (including the dashboard,
        /// which intentionally skips GetAccessTokenAsync to keep the front
        /// page free of network IO) sees a populated session even on the
        /// very first hit after a redeploy wipes the in-memory session
        /// store. Idempotent: bails immediately when session already has
        /// AccessToken, so the cost is one no-op session read per request
        /// after the first one in a given session.
        /// </summary>
        public static async Task TryRehydrateSessionFromCookieAsync(HttpContext ctx, IConfigStore configStore)
        {
            if (ctx == null || configStore == null) return;
            if (!string.IsNullOrEmpty(ctx.Session.GetString("AccessToken"))) return;
            var uidCookie = ctx.Request.Cookies[UidCookieName];
            if (string.IsNullOrEmpty(uidCookie)) return;
            var tokenData = await configStore.GetAsync(uidCookie);
            if (tokenData != null)
            {
                ctx.Session.SetString("AccessToken", SerializeObject(tokenData));
            }
        }

        public void SetPrimaryUidCookie(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx == null) return;
            ctx.Response.Cookies.Append(UidCookieName, uid, new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,        // out of reach of any in-page JS
                IsEssential = true,     // can't be suppressed by future consent middleware
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Secure = ctx.Request.IsHttps,  // HTTPS-only in prod; permissive for local http dev
                MaxAge = TimeSpan.FromDays(365),
            });
        }

        public void ClearPrimaryUidCookie()
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx == null) return;
            ctx.Response.Cookies.Delete(UidCookieName);
        }

        #region Anilist
        public async Task<TokenData> GetAccessTokenByCodeAsync(string code, bool setSession = true)
        {
            var client = _clientFactory.CreateClient();
            var response = await client.PostAsync("https://anilist.co/api/v2/oauth/token", new FormUrlEncodedContent(new[]
            {
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("client_id", AnilistClientId),
            new KeyValuePair<string, string>("client_secret", AnilistClientSecret),
            new KeyValuePair<string, string>("redirect_uri", AnilistRedirectUri),
            new KeyValuePair<string, string>("code", code)
            }));

            return await ParseAnilistTokenResponseAsync(response, setSession);
        }

        private async Task<TokenData> RefreshAccessToken(string refreshToken)
        {
            var requestData = new FormUrlEncodedContent(new[]
            {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", AnilistClientId),
            new KeyValuePair<string, string>("client_secret", AnilistClientSecret),
            new KeyValuePair<string, string>("refresh_token", refreshToken)
        });

            var response = await _clientFactory.CreateClient().PostAsync("https://anilist.co/api/v2/oauth/token", requestData);

            // Refresh-driven exchange: never overwrites the active session — that's reserved
            // for the initial login response.
            return await ParseAnilistTokenResponseAsync(response, setSession: false);
        }

        private async Task<TokenData> ParseAnilistTokenResponseAsync(HttpResponseMessage response, bool setSession)
        {
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var tokenData = DeserializeObject<TokenData>(content);
            if (!string.IsNullOrEmpty(tokenData?.access_token))
            {
                tokenData.anime_service = AnimeService.Anilist;
                tokenData.expiration_date = DateTime.UtcNow.AddSeconds(tokenData.expires_in ?? 0);
                var handler = new JwtSecurityTokenHandler();
                var jwtToken = handler.ReadJwtToken(tokenData.access_token);
                tokenData.user_id = jwtToken.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
                // One-off Viewer query for the user's score scale so cross-service score
                // fan-out doesn't have to guess whether a "10" means 10/10 or 10/100 (the
                // heuristic before this branched at >10, which silently mis-shipped a
                // 100-scale user's exact 10/100 rating as 10/10). Best-effort: a transient
                // network blip leaves score_format null and the normaliser falls back to
                // the heuristic, so login isn't blocked on this.
                tokenData.score_format = await FetchAnilistScoreFormatAsync(tokenData.access_token);
                if (setSession)
                    _httpContextAccessor.HttpContext?.Session.SetString("AccessToken", SerializeObject(tokenData));
            }

            return tokenData;
        }

        private async Task<string> FetchAnilistScoreFormatAsync(string accessToken)
        {
            try
            {
                var body = SerializeObject(new
                {
                    query = "{ Viewer { mediaListOptions { scoreFormat } } }",
                });
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var resp = await _clientFactory.CreateClient().SendAsync(request);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync();
                dynamic parsed = DeserializeObject<dynamic>(json);
                return parsed?.data?.Viewer?.mediaListOptions?.scoreFormat;
            }
            catch
            {
                return null;
            }
        }
        #endregion Anilist

        #region Kitsu
        private const string KitsuTokenUrl = "https://kitsu.io/api/oauth/token";

        /// <summary>
        /// Builds a Kitsu OAuth token request. Kitsu's docs require the body to be
        /// <b>RFC3986</b>-percent-encoded — NOT the legacy <c>application/x-www-form-urlencoded</c>
        /// serialization <c>FormUrlEncodedContent</c> emits ('+' for spaces, under-escaping) —
        /// or a correct password with a space/'+'/'&amp;'/'%' is mangled and Kitsu answers
        /// <c>invalid_grant</c>. <c>Uri.EscapeDataString</c> implements RFC3986. A User-Agent is
        /// also set because Kitsu's Cloudflare front 403s requests without one.
        /// </summary>
        private static HttpRequestMessage BuildKitsuTokenRequest(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var body = string.Join("&", fields.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            var request = new HttpRequestMessage(HttpMethod.Post, KitsuTokenUrl)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            request.Headers.UserAgent.ParseAdd("AniSync/1.0 (+https://github.com/ttsiligkoudis/AniSync)");
            request.Headers.Accept.ParseAdd("application/json");
            return request;
        }

        public async Task<TokenData> GetAccessTokenByCredsAsync(string username, string password, bool setContext = false, string userId = null)
        {
            var context = _httpContextAccessor.HttpContext;

            if (string.IsNullOrEmpty(username))
                return null;

            var request = BuildKitsuTokenRequest(new[]
            {
                new KeyValuePair<string, string>("grant_type", "password"),
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password),
            });

            var response = await _clientFactory.CreateClient().SendAsync(request);
            // The password is intentionally never written into TokenData. Refreshes are
            // driven by the refresh_token Kitsu hands back here (RefreshKitsuAccessToken
            // below); the password we just used for the password grant is dropped from
            // memory as soon as this method returns.
            return await ParseKitsuTokenResponseAsync(response, username, userId, setContext);
        }

        /// <summary>
        /// Mirrors <see cref="RefreshAccessToken"/> for Kitsu. Kitsu's OAuth (Doorkeeper)
        /// returns a refresh_token alongside every password-grant response and accepts the
        /// standard <c>grant_type=refresh_token</c> exchange. We use that here so the user's
        /// plaintext password never has to be persisted just to keep the access_token alive.
        /// </summary>
        private async Task<TokenData> RefreshKitsuAccessToken(string refreshToken, string username, string userId)
        {
            if (string.IsNullOrEmpty(refreshToken)) return null;

            var request = BuildKitsuTokenRequest(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
            });

            var response = await _clientFactory.CreateClient().SendAsync(request);
            // Refresh path: never touches the session — that's reserved for the initial
            // login response, same convention as the AniList/MAL refresh helpers.
            return await ParseKitsuTokenResponseAsync(response, username, userId, setContext: false);
        }

        private async Task<TokenData> ParseKitsuTokenResponseAsync(HttpResponseMessage response, string username, string userId, bool setContext)
        {
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var tokenData = DeserializeObject<TokenData>(content);
            if (string.IsNullOrEmpty(tokenData?.access_token)) return null;

            tokenData.anime_service = AnimeService.Kitsu;
            tokenData.expiration_date = DateTime.UtcNow.AddSeconds(tokenData.expires_in ?? 0);
            tokenData.username = username;
            // The refresh response doesn't echo identity back, so preserve the caller's
            // user_id when we have one — same pattern as MAL's refresh helper. On the
            // initial password grant userId is null so we resolve it via /users?self.
            tokenData.user_id = !string.IsNullOrEmpty(userId)
                ? userId
                : await GetUserIdAsync(tokenData.access_token);

            if (setContext)
            {
                _httpContextAccessor.HttpContext?.Session.SetString("AccessToken", SerializeObject(tokenData));
            }

            return tokenData;
        }

        public async Task<string> GetUserIdAsync(string accessToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://kitsu.io/api/edge/users?filter[self]=true");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var user = DeserializeObject<dynamic>(content);

            return user.data[0].id;
        }
        #endregion Kitsu

        #region MyAnimeList
        public async Task<TokenData> GetAccessTokenByMalCodeAsync(string code, string codeVerifier, bool setSession = true)
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                new("client_id", MalClientId ?? string.Empty),
                new("code", code ?? string.Empty),
                new("code_verifier", codeVerifier ?? string.Empty),
                new("grant_type", "authorization_code"),
                new("redirect_uri", MalRedirectUri ?? string.Empty),
            };
            // The client_secret is only required for confidential MAL apps; public apps
            // omit it. Send it when configured so both flavours work.
            if (!string.IsNullOrEmpty(MalClientSecret))
                fields.Add(new("client_secret", MalClientSecret));

            var response = await _clientFactory.CreateClient()
                .PostAsync("https://myanimelist.net/v1/oauth2/token", new FormUrlEncodedContent(fields));

            // Initial code-exchange: always fetch identity. setSession is decoupled because
            // the link-provider flow wants the user info but must not overwrite the primary's
            // session entry.
            return await ParseMalTokenResponseAsync(response, fetchUser: true, setSession: setSession);
        }

        private async Task<TokenData> RefreshMalAccessToken(string refreshToken)
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                new("client_id", MalClientId ?? string.Empty),
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken ?? string.Empty),
            };
            if (!string.IsNullOrEmpty(MalClientSecret))
                fields.Add(new("client_secret", MalClientSecret));

            var response = await _clientFactory.CreateClient()
                .PostAsync("https://myanimelist.net/v1/oauth2/token", new FormUrlEncodedContent(fields));

            // Refresh: don't re-fetch identity (the caller patches user_id/username back
            // from the prior token) and never write to the session.
            return await ParseMalTokenResponseAsync(response, fetchUser: false, setSession: false);
        }

        private async Task<TokenData> ParseMalTokenResponseAsync(HttpResponseMessage response, bool fetchUser, bool setSession)
        {
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var tokenData = DeserializeObject<TokenData>(content);
            if (string.IsNullOrEmpty(tokenData?.access_token)) return null;

            tokenData.anime_service = AnimeService.MyAnimeList;
            tokenData.expiration_date = DateTime.UtcNow.AddSeconds(tokenData.expires_in ?? 0);

            if (fetchUser)
            {
                var (userId, userName) = await GetMalUserAsync(tokenData.access_token);
                tokenData.user_id = userId;
                tokenData.username = userName;
            }

            if (setSession)
                _httpContextAccessor.HttpContext?.Session.SetString("AccessToken", SerializeObject(tokenData));

            return tokenData;
        }

        public async Task<TokenData> RefreshLinkedTokenAsync(TokenData token)
        {
            if (token == null) return null;

            switch (token.anime_service)
            {
                case AnimeService.Anilist:
                    if (string.IsNullOrEmpty(token.refresh_token)) return null;
                    return await RefreshAccessToken(token.refresh_token);

                case AnimeService.MyAnimeList:
                    if (string.IsNullOrEmpty(token.refresh_token)) return null;
                    var refreshed = await RefreshMalAccessToken(token.refresh_token);
                    if (refreshed == null) return null;
                    // MAL refresh responses don't echo the user info back; preserve identity
                    // from the prior linked token so the cached row stays usable.
                    refreshed.user_id ??= token.user_id;
                    refreshed.username ??= token.username;
                    return refreshed;

                case AnimeService.Trakt:
                    // Trakt rotates the refresh_token on every exchange; delegate to
                    // TraktService which carries the prior identity forward.
                    if (string.IsNullOrEmpty(token.refresh_token)) return null;
                    return await _traktService.RefreshTokenAsync(token);

                case AnimeService.Kitsu:
                    // Kitsu's password grant returns a refresh_token (Doorkeeper); we now
                    // exchange that for a fresh access_token instead of replaying the
                    // user's password. A missing refresh_token means either a legacy
                    // pre-refresh-token row or an anonymous linked token; either way,
                    // there's nothing we can do without sending the user back through
                    // the Kitsu login form, so return null to surface needs-reauth.
                    if (string.IsNullOrEmpty(token.refresh_token)) return null;
                    return await RefreshKitsuAccessToken(token.refresh_token, token.username, token.user_id);

                default:
                    return null;
            }
        }

        private async Task<(string userId, string userName)> GetMalUserAsync(string accessToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.myanimelist.net/v2/users/@me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _clientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode) return (null, null);

            var content = await response.Content.ReadAsStringAsync();
            var user = DeserializeObject<dynamic>(content);
            return ((string)user?.id?.ToString(), (string)user?.name);
        }
        #endregion MyAnimeList
    }
}

