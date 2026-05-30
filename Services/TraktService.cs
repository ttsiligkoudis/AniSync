using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;

namespace AnimeList.Services
{
    public class TraktService : ITraktService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IConfiguration _configuration;
        private readonly IConfigStore _configStore;
        private readonly ILogger<TraktService> _logger;

        private const string ApiBase = "https://api.trakt.tv";
        private const string AuthorizeBase = "https://trakt.tv/oauth/authorize";

        public TraktService(
            IHttpClientFactory clientFactory,
            IConfiguration configuration,
            IConfigStore configStore,
            ILogger<TraktService> logger)
        {
            _clientFactory = clientFactory;
            _configuration = configuration;
            _configStore = configStore;
            _logger = logger;
        }

        private string ClientId => _configuration["Trakt:ClientId"];
        private string ClientSecret => _configuration["Trakt:ClientSecret"];
        private string RedirectUri => _configuration["Trakt:RedirectUri"];

        public bool IsConfigured =>
            !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret) && !string.IsNullOrEmpty(RedirectUri);

        public string BuildAuthorizeUrl(string state) =>
            $"{AuthorizeBase}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&state={Uri.EscapeDataString(state)}";

        public async Task<TraktToken> ExchangeCodeAsync(string code)
        {
            if (!IsConfigured || string.IsNullOrEmpty(code)) return null;

            var token = await PostTokenAsync(new JObject
            {
                ["code"] = code,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"] = RedirectUri,
                ["grant_type"] = "authorization_code",
            });
            if (token == null) return null;

            token.username = await FetchUsernameAsync(token.access_token);
            return token;
        }

        public async Task<TraktToken> GetValidTokenAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            var token = await _configStore.GetTraktTokenAsync(uid);
            if (token == null || !token.Connected) return null;

            // Refresh a few minutes ahead of expiry so an in-flight API call
            // doesn't race the boundary. No expiry recorded → assume valid.
            var needsRefresh = token.expiration_date.HasValue
                && token.expiration_date.Value <= DateTime.UtcNow.AddMinutes(5);
            if (!needsRefresh) return token;

            var refreshed = await RefreshAsync(token);
            if (refreshed == null)
            {
                // Unrecoverable — clear so the UI prompts a reconnect rather
                // than retrying a dead token on every render.
                _logger.LogInformation("Trakt token refresh failed for uid {Uid}; clearing connection.", uid);
                await _configStore.ClearTraktTokenAsync(uid);
                return null;
            }

            refreshed.username = token.username; // carry over; /settings unchanged on refresh
            await _configStore.SetTraktTokenAsync(uid, refreshed);
            return refreshed;
        }

        private async Task<TraktToken> RefreshAsync(TraktToken token)
        {
            if (!IsConfigured || string.IsNullOrEmpty(token?.refresh_token)) return null;

            return await PostTokenAsync(new JObject
            {
                ["refresh_token"] = token.refresh_token,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"] = RedirectUri,
                ["grant_type"] = "refresh_token",
            });
        }

        /// <summary>
        /// POSTs to /oauth/token (authorization_code or refresh_token grant) and
        /// parses the common token response shape. Returns null on any failure.
        /// </summary>
        private async Task<TraktToken> PostTokenAsync(JObject body)
        {
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                using var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{ApiBase}/oauth/token", content);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trakt token endpoint returned {Status}.", (int)response.StatusCode);
                    return null;
                }

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                var accessToken = (string)json["access_token"];
                if (string.IsNullOrEmpty(accessToken)) return null;

                // Trakt returns created_at (unix) + expires_in (seconds). Prefer
                // created_at when present; fall back to "now" so the absolute
                // expiry is always populated.
                var expiresIn = (long?)json["expires_in"] ?? 0;
                var createdAt = (long?)json["created_at"];
                var baseTime = createdAt.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(createdAt.Value).UtcDateTime
                    : DateTime.UtcNow;

                return new TraktToken
                {
                    access_token = accessToken,
                    refresh_token = (string)json["refresh_token"],
                    expiration_date = expiresIn > 0 ? baseTime.AddSeconds(expiresIn) : null,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt token exchange failed.");
                return null;
            }
        }

        private async Task<string> FetchUsernameAsync(string accessToken)
        {
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);

                using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/users/settings");
                req.Headers.Add("trakt-api-version", "2");
                req.Headers.Add("trakt-api-key", ClientId);
                req.Headers.Add("Authorization", $"Bearer {accessToken}");

                var response = await client.SendAsync(req);
                if (!response.IsSuccessStatusCode) return null;

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                return (string)json["user"]?["username"]
                    ?? (string)json["user"]?["ids"]?["slug"];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt /users/settings lookup failed.");
                return null;
            }
        }
    }
}
