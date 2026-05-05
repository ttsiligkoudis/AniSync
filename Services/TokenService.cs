using AnimeList.Models;
using AnimeList.Services.Interfaces;
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
        private static readonly ConcurrentDictionary<string, TokenData> _kitsuTokenCache = new();
        private static readonly ConcurrentDictionary<string, TokenData> _anilistTokenCache = new();
        private static readonly ConcurrentDictionary<string, TokenData> _malTokenCache = new();

        public TokenService(IHttpClientFactory clientFactory, IHttpContextAccessor httpContextAccessor, IConfigStore configStore, IConfiguration configuration)
        {
            _clientFactory = clientFactory;
            _httpContextAccessor = httpContextAccessor;
            _configStore = configStore;
            _configuration = configuration;
        }

        // Pulled from configuration so deployments can supply their own MyAnimeList app
        // credentials without rebuilding. The redirect URI must match what's registered
        // on the MAL developer dashboard for that client id.
        private string MalClientId => _configuration["Mal:ClientId"];
        private string MalClientSecret => _configuration["Mal:ClientSecret"];
        private string MalRedirectUri => _configuration["Mal:RedirectUri"];

        public async Task<TokenData> GetAccessTokenAsync(string config = null)
        {
            TokenData tokenData = null;

            if (!string.IsNullOrEmpty(config))
            {
                var configuration = DecodeConfig(config);

                if (!string.IsNullOrEmpty(configuration?.tokenUid))
                {
                    // v4: token JSON lives in the config store, not the URL.
                    tokenData = await _configStore.GetAsync(configuration.tokenUid);
                }
                else if (!string.IsNullOrEmpty(configuration?.tokenData))
                {
                    // v1/v2/v3: token JSON inline in the URL.
                    tokenData = DeserializeObject<TokenData>(configuration.tokenData);
                }
            }
            else
            {
                var sessionStr = _httpContextAccessor.HttpContext.Session.GetString("AccessToken");
                if (!string.IsNullOrEmpty(sessionStr))
                    tokenData = DeserializeObject<TokenData>(sessionStr);
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
                    && _anilistTokenCache.TryGetValue(cacheKey, out var cached)
                    && !IsTokenExpired(cached.expiration_date))
                {
                    tokenData = cached;
                }
                else if (IsTokenExpired(tokenData.expiration_date) && !string.IsNullOrEmpty(tokenData.refresh_token))
                {
                    var refreshed = await RefreshAccessToken(tokenData.refresh_token);
                    if (refreshed != null && !string.IsNullOrEmpty(cacheKey))
                    {
                        _anilistTokenCache[cacheKey] = refreshed;
                        // Write back to the config store so v4 install URLs survive token rotation.
                        await _configStore.UpdateByUserAsync(refreshed);
                    }
                    tokenData = refreshed;
                }
            }
            else if (tokenData.anime_service == AnimeService.MyAnimeList)
            {
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return null;

                var cacheKey = tokenData.user_id;
                if (!string.IsNullOrEmpty(cacheKey)
                    && _malTokenCache.TryGetValue(cacheKey, out var cached)
                    && !IsTokenExpired(cached.expiration_date))
                {
                    tokenData = cached;
                }
                else if (IsTokenExpired(tokenData.expiration_date) && !string.IsNullOrEmpty(tokenData.refresh_token))
                {
                    var refreshed = await RefreshMalAccessToken(tokenData.refresh_token);
                    if (refreshed != null && !string.IsNullOrEmpty(cacheKey))
                    {
                        // Preserve identity fields the refresh response doesn't echo back
                        refreshed.user_id ??= tokenData.user_id;
                        refreshed.username ??= tokenData.username;
                        _malTokenCache[cacheKey] = refreshed;
                        await _configStore.UpdateByUserAsync(refreshed);
                    }
                    tokenData = refreshed;
                }
            }
            else
            {
                var cacheKey = tokenData.username;
                if (!string.IsNullOrEmpty(cacheKey)
                    && _kitsuTokenCache.TryGetValue(cacheKey, out var cached)
                    && !IsTokenExpired(cached.expiration_date))
                {
                    tokenData = cached;
                }
                else
                {
                    var refreshed = await GetAccessTokenByCredsAsync(tokenData.username, tokenData.password, false, tokenData.user_id);
                    if (refreshed != null && !string.IsNullOrEmpty(cacheKey))
                    {
                        _kitsuTokenCache[cacheKey] = refreshed;
                        await _configStore.UpdateByUserAsync(refreshed);
                    }
                    tokenData = refreshed;
                }
            }

            return tokenData?.Clone();
        }

        public async Task RemoveCachedUser()
        {
            var tokenData = await GetAccessTokenAsync();

            if (tokenData == null) return;

            if (tokenData.anime_service == AnimeService.Anilist && !string.IsNullOrEmpty(tokenData.user_id))
            {
                _anilistTokenCache.TryRemove(tokenData.user_id, out _);
            }
            else if (tokenData.anime_service == AnimeService.MyAnimeList && !string.IsNullOrEmpty(tokenData.user_id))
            {
                _malTokenCache.TryRemove(tokenData.user_id, out _);
            }
            else if (tokenData.anime_service == AnimeService.Kitsu && !string.IsNullOrEmpty(tokenData.username))
            {
                _kitsuTokenCache.TryRemove(tokenData.username, out _);
            }

            _httpContextAccessor.HttpContext.Session.Remove("AccessToken");
        }

        #region Anilist
        public async Task<TokenData> GetAccessTokenByCodeAsync(string code, bool setSession = true)
        {
            var client = _clientFactory.CreateClient();
            var response = await client.PostAsync("https://anilist.co/api/v2/oauth/token", new FormUrlEncodedContent(new[]
            {
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("redirect_uri", redirectUri),
            new KeyValuePair<string, string>("code", code)
            }));

            return await ParseAnilistTokenResponseAsync(response, setSession);
        }

        private async Task<TokenData> RefreshAccessToken(string refreshToken)
        {
            var requestData = new FormUrlEncodedContent(new[]
            {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
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
                if (setSession)
                    _httpContextAccessor.HttpContext?.Session.SetString("AccessToken", SerializeObject(tokenData));
            }

            return tokenData;
        }
        #endregion Anilist

        #region Kitsu
        public async Task<TokenData> GetAccessTokenByCredsAsync(string username, string password, bool setContext = false, string userId = null)
        {
            var context = _httpContextAccessor.HttpContext;
            TokenData tokenData = null;

            if (!string.IsNullOrEmpty(username))
            {
                var requestBody = new Dictionary<string, string>
                {
                    { "grant_type", "password" },
                    { "username", username },
                    { "password", password }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, "https://kitsu.io/api/oauth/token")
                {
                    Content = new FormUrlEncodedContent(requestBody)
                };

                var response = await _clientFactory.CreateClient().SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                tokenData = DeserializeObject<TokenData>(content);
                if (!string.IsNullOrEmpty(tokenData?.access_token))
                {
                    tokenData.anime_service = AnimeService.Kitsu;
                    tokenData.expiration_date = DateTime.UtcNow.AddSeconds(tokenData.expires_in ?? 0);
                    tokenData.username = username;
                    tokenData.password = password;
                    tokenData.user_id = !string.IsNullOrEmpty(userId)
                        ? userId
                        : await GetUserIdAsync(tokenData.access_token);
                    if (setContext)
                    {
                        context.Session.SetString("AccessToken", SerializeObject(tokenData));
                    }
                }
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

