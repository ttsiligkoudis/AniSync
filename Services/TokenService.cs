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
        private static readonly ConcurrentDictionary<string, TokenData> _kitsuTokenCache = new();
        private static readonly ConcurrentDictionary<string, TokenData> _anilistTokenCache = new();

        public TokenService(IHttpClientFactory clientFactory, IHttpContextAccessor httpContextAccessor)
        {
            _clientFactory = clientFactory;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<TokenData> GetAccessTokenAsync(string config = null)
        {
            string tokenDataStr;

            if (!string.IsNullOrEmpty(config))
            {
                var configuration = DeserializeObject<Configuration>(config);

                tokenDataStr = DecompressString(Uri.UnescapeDataString(configuration.tokenData));
            }
            else
            {
                tokenDataStr = _httpContextAccessor.HttpContext.Session.GetString("AccessToken");
            }

            if (string.IsNullOrEmpty(tokenDataStr))
                return null;

            var tokenData = DeserializeObject<TokenData>(tokenDataStr);

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
            new KeyValuePair<string, string>("client_id", "20850"),
            new KeyValuePair<string, string>("client_secret", "bAgns7Q0rGxXnhGRRoq84slYleN4NIe2SkoSDOZ1"),
            new KeyValuePair<string, string>("redirect_uri", "https://anisync.fly.dev/Auth/Callback"),
            new KeyValuePair<string, string>("code", code)
            }));

            return await ParseAnilistTokenResponseAsync(response);
        }

        private async Task<TokenData> RefreshAccessToken(string refreshToken)
        {
            var requestData = new FormUrlEncodedContent(new[]
            {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", "20850"),
            new KeyValuePair<string, string>("client_secret", "bAgns7Q0rGxXnhGRRoq84slYleN4NIe2SkoSDOZ1"),
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
            TokenData tokenData;

            if (string.IsNullOrEmpty(username))
            {
                tokenData = await CreateAnonymousKitsuToken();

                if (setContext)
                {
                    context.Session.SetString("AccessToken", SerializeObject(tokenData));
                }
            }
            else
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

        public async Task<TokenData> CreateAnonymousKitsuToken()
        {
            return new TokenData
            {
                anime_service = AnimeService.Kitsu
            };
        }
        #endregion Kitsu
    }
}

