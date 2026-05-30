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

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/oauth/token")
                {
                    Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json"),
                };
                // Trakt's API sits behind Cloudflare, which 403s requests with no
                // User-Agent. HttpClient sends none by default.
                req.Headers.TryAddWithoutValidation("User-Agent", "AniSync/1.0");

                var response = await client.SendAsync(req);
                var payload = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trakt token endpoint returned {Status}: {Body}",
                        (int)response.StatusCode, Truncate(payload, 500));
                    return null;
                }

                var json = JObject.Parse(payload);
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

        // ── Reads ───────────────────────────────────────────────────────────

        public async Task<List<TraktListItem>> GetWatchlistAsync(string uid)
        {
            var arr = await GetAuthedAsync(uid, "/sync/watchlist?extended=full") as JArray;
            if (arr == null) return new();

            var items = new List<TraktListItem>();
            foreach (var it in arr)
            {
                var t = (string)it["type"];
                if (t == "movie") items.Add(MovieItem(it["movie"]));
                else if (t == "show") items.Add(ShowItem(it["show"]));
            }
            return items.Where(i => !string.IsNullOrEmpty(i.ImdbId)).ToList();
        }

        public async Task<List<TraktListItem>> GetPlaybackAsync(string uid)
        {
            var arr = await GetAuthedAsync(uid, "/sync/playback?extended=full") as JArray;
            if (arr == null) return new();

            var items = new List<TraktListItem>();
            foreach (var it in arr)
            {
                var t = (string)it["type"];
                var progress = (double?)it["progress"];
                var pausedAt = (DateTime?)it["paused_at"];

                if (t == "movie")
                {
                    var m = MovieItem(it["movie"]);
                    m.Progress = progress; m.PausedAt = pausedAt;
                    items.Add(m);
                }
                else if (t == "episode")
                {
                    var s = ShowItem(it["show"]);
                    s.Season = (int?)it["episode"]?["season"];
                    s.Episode = (int?)it["episode"]?["number"];
                    s.Progress = progress; s.PausedAt = pausedAt;
                    items.Add(s);
                }
            }
            return items
                .Where(i => !string.IsNullOrEmpty(i.ImdbId))
                .OrderByDescending(i => i.PausedAt)
                .ToList();
        }

        // ── Writes ──────────────────────────────────────────────────────────

        public Task<bool> AddToWatchlistAsync(string uid, string type, string imdbId) =>
            string.IsNullOrEmpty(imdbId)
                ? Task.FromResult(false)
                : PostAuthedAsync(uid, "/sync/watchlist", SyncBody(type, imdbId, null, null));

        public Task<bool> RemoveFromWatchlistAsync(string uid, string type, string imdbId) =>
            string.IsNullOrEmpty(imdbId)
                ? Task.FromResult(false)
                : PostAuthedAsync(uid, "/sync/watchlist/remove", SyncBody(type, imdbId, null, null));

        public Task<bool> AddToHistoryAsync(string uid, string type, string imdbId, int? season, int? episode) =>
            string.IsNullOrEmpty(imdbId)
                ? Task.FromResult(false)
                : PostAuthedAsync(uid, "/sync/history", SyncBody(type, imdbId, season, episode));

        public Task<bool> ScrobbleAsync(string uid, string action, string type, string imdbId, int? season, int? episode, double progress)
        {
            var a = action?.ToLowerInvariant();
            if (string.IsNullOrEmpty(imdbId) || (a != "start" && a != "pause" && a != "stop"))
                return Task.FromResult(false);
            return PostAuthedAsync(uid, $"/scrobble/{a}", ScrobbleBody(type, imdbId, season, episode, progress));
        }

        // ── Shared HTTP + body helpers ──────────────────────────────────────

        private HttpRequestMessage BuildApiRequest(HttpMethod method, string path, string accessToken, JToken body = null)
        {
            var req = new HttpRequestMessage(method, $"{ApiBase}{path}");
            req.Headers.TryAddWithoutValidation("User-Agent", "AniSync/1.0");
            req.Headers.Add("trakt-api-version", "2");
            req.Headers.Add("trakt-api-key", ClientId);
            req.Headers.Add("Authorization", $"Bearer {accessToken}");
            if (body != null)
                req.Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");
            return req;
        }

        private async Task<JToken> GetAuthedAsync(string uid, string path)
        {
            var token = await GetValidTokenAsync(uid);
            if (token == null) return null;
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var req = BuildApiRequest(HttpMethod.Get, path, token.access_token);
                var resp = await client.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trakt GET {Path} returned {Status}.", path, (int)resp.StatusCode);
                    return null;
                }
                return JToken.Parse(await resp.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt GET {Path} failed.", path);
                return null;
            }
        }

        private async Task<bool> PostAuthedAsync(string uid, string path, JObject body)
        {
            var token = await GetValidTokenAsync(uid);
            if (token == null) return false;
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var req = BuildApiRequest(HttpMethod.Post, path, token.access_token, body);
                var resp = await client.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    var b = await resp.Content.ReadAsStringAsync();
                    _logger.LogWarning("Trakt POST {Path} returned {Status}: {Body}", path, (int)resp.StatusCode, Truncate(b, 300));
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt POST {Path} failed.", path);
                return false;
            }
        }

        private static TraktListItem MovieItem(JToken m) => new()
        {
            Type = "movie",
            ImdbId = (string)m?["ids"]?["imdb"],
            Title = (string)m?["title"],
            Year = (int?)m?["year"],
        };

        private static TraktListItem ShowItem(JToken s) => new()
        {
            Type = "series",
            ImdbId = (string)s?["ids"]?["imdb"],
            Title = (string)s?["title"],
            Year = (int?)s?["year"],
        };

        // Grouped body for /sync/* endpoints (watchlist, history). A movie
        // rides under "movies"; a show under "shows" — with seasons/episodes
        // nested when a specific episode is targeted.
        private static JObject SyncBody(string type, string imdbId, int? season, int? episode)
        {
            var ids = new JObject { ["imdb"] = imdbId };
            if (type == "movie")
                return new JObject { ["movies"] = new JArray { new JObject { ["ids"] = ids } } };

            var show = new JObject { ["ids"] = ids };
            if (season.HasValue && episode.HasValue)
            {
                show["seasons"] = new JArray
                {
                    new JObject
                    {
                        ["number"] = season.Value,
                        ["episodes"] = new JArray { new JObject { ["number"] = episode.Value } },
                    },
                };
            }
            return new JObject { ["shows"] = new JArray { show } };
        }

        // Single-item body for /scrobble/{action}: a flat movie/show(+episode)
        // alongside the playback progress percent.
        private static JObject ScrobbleBody(string type, string imdbId, int? season, int? episode, double progress)
        {
            var ids = new JObject { ["imdb"] = imdbId };
            var body = new JObject { ["progress"] = progress };
            if (type == "movie")
            {
                body["movie"] = new JObject { ["ids"] = ids };
            }
            else
            {
                body["show"] = new JObject { ["ids"] = ids };
                if (season.HasValue && episode.HasValue)
                    body["episode"] = new JObject { ["season"] = season.Value, ["number"] = episode.Value };
            }
            return body;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));

        private async Task<string> FetchUsernameAsync(string accessToken)
        {
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);

                using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/users/settings");
                req.Headers.TryAddWithoutValidation("User-Agent", "AniSync/1.0");
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
