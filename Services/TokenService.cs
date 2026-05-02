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
        private static readonly ConcurrentDictionary<string, TokenData> _kitsuTokenCache = new();
        private static readonly ConcurrentDictionary<string, TokenData> _anilistTokenCache = new();

        public TokenService(IHttpClientFactory clientFactory, IHttpContextAccessor httpContextAccessor, IConfigStore configStore)
        {
            _clientFactory = clientFactory;
            _httpContextAccessor = httpContextAccessor;
            _configStore = configStore;
        }

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

            return tokenData.Clone();
        }

        public async Task RemoveCachedUser()
        {
            var tokenData = await GetAccessTokenAsync();

            if (tokenData == null) return;

            if (tokenData.anime_service == AnimeService.Anilist && !string.IsNullOrEmpty(tokenData.user_id))
            {
                _anilistTokenCache.TryRemove(tokenData.user_id, out _);
            }
            else if (tokenData.anime_service == AnimeService.Kitsu && !string.IsNullOrEmpty(tokenData.username))
            {
                _kitsuTokenCache.TryRemove(tokenData.username, out _);
            }

            _httpContextAccessor.HttpContext.Session.Remove("AccessToken");
        }

        #region Anilist
        public async Task<TokenData> GetAccessTokenByCodeAsync(string code)
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

            return await ParseAnilistTokenResponseAsync(response);
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

            return await ParseAnilistTokenResponseAsync(response);
        }

        private async Task<TokenData> ParseAnilistTokenResponseAsync(HttpResponseMessage response)
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
    }
}

