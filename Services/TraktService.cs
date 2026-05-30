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
        private readonly IAnimeMappingService _mappingService;
        private readonly ILogger<TraktService> _logger;

        private const string ApiBase = "https://api.trakt.tv";
        private const string AuthorizeBase = "https://trakt.tv/oauth/authorize";

        public TraktService(
            IHttpClientFactory clientFactory,
            IConfiguration configuration,
            IConfigStore configStore,
            IAnimeMappingService mappingService,
            ILogger<TraktService> logger)
        {
            _clientFactory = clientFactory;
            _configuration = configuration;
            _configStore = configStore;
            _mappingService = mappingService;
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

        public async Task<TokenData> ExchangeCodeAsync(string code)
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

            // Resolve the account identity once at exchange time. The slug is stable and
            // doubles as the identity key (GetUserKey/IdentityColumnFor → trakt_user_key),
            // so a re-login dedups onto the same config row.
            var (username, slug) = await FetchUserAsync(token.access_token);
            token.username = username;
            token.user_id = slug ?? username;
            return token;
        }

        public async Task<TokenData> RefreshTokenAsync(TokenData token)
        {
            if (!IsConfigured || string.IsNullOrEmpty(token?.refresh_token)) return null;

            var refreshed = await PostTokenAsync(new JObject
            {
                ["refresh_token"] = token.refresh_token,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"] = RedirectUri,
                ["grant_type"] = "refresh_token",
            });
            if (refreshed == null) return null;

            // Refresh responses don't echo identity back — carry it over so the persisted
            // token (and its identity column) stays usable. Same pattern as MAL/Kitsu.
            refreshed.user_id ??= token.user_id;
            refreshed.username ??= token.username;
            return refreshed;
        }

        public async Task<TokenData> GetValidTokenAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            // Resolve the Trakt token from whichever slot holds it: the row's primary when
            // the user signed in with Trakt, otherwise a linked secondary.
            var primary = await _configStore.GetAsync(uid);
            var isPrimary = primary?.anime_service == AnimeService.Trakt;
            var token = isPrimary
                ? primary
                : (await _configStore.GetLinkedTokensAsync(uid))
                    .FirstOrDefault(l => l.Service == AnimeService.Trakt && !l.NeedsReauth)?.TokenData;

            if (token == null || string.IsNullOrEmpty(token.access_token)) return null;

            // Refresh a few minutes ahead of expiry so an in-flight API call
            // doesn't race the boundary. No expiry recorded → assume valid.
            var needsRefresh = token.expiration_date.HasValue
                && token.expiration_date.Value <= DateTime.UtcNow.AddMinutes(5);
            if (!needsRefresh) return token;

            var refreshed = await RefreshTokenAsync(token);
            if (refreshed == null)
            {
                _logger.LogInformation("Trakt token refresh failed for uid {Uid}.", uid);
                return null;
            }

            // Persist the refresh back to the slot it came from.
            if (isPrimary)
            {
                await _configStore.UpdateByUserAsync(refreshed);
            }
            else
            {
                await _configStore.SetLinkedTokenAsync(uid, new LinkedToken
                {
                    Service = AnimeService.Trakt,
                    TokenData = refreshed,
                    NeedsReauth = false,
                });
            }
            return refreshed;
        }

        /// <summary>
        /// POSTs to /oauth/token (authorization_code or refresh_token grant) and parses the
        /// common token response shape into a Trakt-tagged <see cref="TokenData"/>. Returns
        /// null on any failure.
        /// </summary>
        private async Task<TokenData> PostTokenAsync(JObject body)
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

                return new TokenData
                {
                    anime_service = AnimeService.Trakt,
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

        public async Task<List<TraktListItem>> GetHistoryAsync(string uid)
        {
            var arr = await GetAuthedAsync(uid, "/sync/history?extended=full&limit=100") as JArray;
            if (arr == null) return new();

            var items = new List<TraktListItem>();
            foreach (var it in arr)
            {
                var t = (string)it["type"];
                if (t == "movie") items.Add(MovieItem(it["movie"]));
                else if (t == "episode") items.Add(ShowItem(it["show"]));
            }
            // History returns one row per watched movie/episode; collapse to one entry per
            // imdb id (a binged series otherwise floods the grid with the same show).
            return items
                .Where(i => !string.IsNullOrEmpty(i.ImdbId))
                .GroupBy(i => i.ImdbId)
                .Select(g => g.First())
                .ToList();
        }

        public async Task<List<TraktListItem>> GetListAsync(string uid, ListType list, MetaType mediaType)
        {
            // Map the AniSync list tabs onto Trakt's three relevant surfaces:
            // Planning → watchlist, Completed → watched history, Current → in-progress
            // playback (continue-watching). The remaining anime tabs (Paused / Dropped /
            // Repeating) have no Trakt analogue, so they come back empty.
            var items = list switch
            {
                ListType.Planning => await GetWatchlistAsync(uid),
                ListType.Completed => await GetHistoryAsync(uid),
                ListType.Current => await GetPlaybackAsync(uid),
                _ => new List<TraktListItem>(),
            };

            var wantType = mediaType == MetaType.movie ? "movie" : "series";
            return items.Where(i => i.Type == wantType).ToList();
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

        // ── Unified-fan-out writes (token-based) ────────────────────────────
        // These are the writes the SyncService fan-out + the manage-entry / auto-track
        // paths use. They take the resolved Trakt TokenData directly (primary or linked)
        // rather than a uid, since the caller already has the token in hand, and they
        // map an anime id → its IMDb show id so anime tracked on AniSync lands on the
        // right Trakt show. Best-effort: a mapping miss (no IMDb id) returns false rather
        // than throwing, so a fan-out target that can't be mapped is skipped cleanly.

        public async Task<bool> SaveEntryAsync(TokenData trakt, string animeId, int? season, int progress, bool planning)
        {
            if (trakt == null || string.IsNullOrEmpty(trakt.access_token)) return false;

            var links = await _mappingService.BuildSourceLinksAsync(animeId);
            var imdb = links?.ImdbId;
            if (string.IsNullOrEmpty(imdb)) return false;

            // Planning → the Trakt watchlist (the show, no episode). Any watched status →
            // add the episode to history so "watched up to N" surfaces in Trakt. Anime are
            // addressed as Trakt shows; the IMDb-side season (shared listing across cours)
            // comes from the mapping, falling back to the caller's season then S1.
            if (planning)
                return await PostJsonAsync(trakt.access_token, "/sync/watchlist", SyncBody("series", imdb, null, null));

            if (progress > 0)
            {
                var imdbSeason = links.ImdbSeason ?? season ?? 1;
                return await PostJsonAsync(trakt.access_token, "/sync/history", SyncBody("series", imdb, imdbSeason, progress));
            }

            return false;
        }

        public async Task<bool> DeleteEntryAsync(TokenData trakt, string animeId, int? season)
        {
            if (trakt == null || string.IsNullOrEmpty(trakt.access_token)) return false;

            var links = await _mappingService.BuildSourceLinksAsync(animeId);
            var imdb = links?.ImdbId;
            if (string.IsNullOrEmpty(imdb)) return false;

            // Removing an entry maps to dropping it from the watchlist (the closest Trakt
            // analogue). History removal would need the specific watched-episode ids and is
            // left out — re-watching just re-adds to history anyway.
            return await PostJsonAsync(trakt.access_token, "/sync/watchlist/remove", SyncBody("series", imdb, null, null));
        }

        // POST against an explicit access token (the token-based write path above), as
        // opposed to PostAuthedAsync which resolves the token from a uid.
        private async Task<bool> PostJsonAsync(string accessToken, string path, JObject body)
        {
            if (string.IsNullOrEmpty(accessToken)) return false;
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var req = BuildApiRequest(HttpMethod.Post, path, accessToken, body);
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

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));

        // Resolves (display username, stable slug) from /users/settings. The slug is the
        // stable identity key; username is for display. Either may be null on failure.
        private async Task<(string username, string slug)> FetchUserAsync(string accessToken)
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
                if (!response.IsSuccessStatusCode) return (null, null);

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                var slug = (string)json["user"]?["ids"]?["slug"];
                var username = (string)json["user"]?["username"] ?? slug;
                return (username, slug);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt /users/settings lookup failed.");
                return (null, null);
            }
        }
    }
}
