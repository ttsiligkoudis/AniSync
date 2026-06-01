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
            var arr = await GetAuthedAsync(uid, "/sync/watchlist?extended=full,images") as JArray;
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
            var arr = await GetAuthedAsync(uid, "/sync/playback?extended=full,images") as JArray;
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

        public async Task<TraktUserStats> GetUserStatsAsync(string uid)
        {
            if (await GetAuthedAsync(uid, "/users/me/stats") is not JObject o) return null;
            try
            {
                var movies = o["movies"];
                var shows = o["shows"];
                var episodes = o["episodes"];
                var movieMinutes = (long?)movies?["minutes"] ?? 0;
                var episodeMinutes = (long?)episodes?["minutes"] ?? 0;
                return new TraktUserStats
                {
                    MoviesWatched = (int?)movies?["watched"] ?? 0,
                    ShowsWatched = (int?)shows?["watched"] ?? 0,
                    EpisodesWatched = (int?)episodes?["watched"] ?? 0,
                    TotalHoursWatched = (int)((movieMinutes + episodeMinutes) / 60),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt user stats parse failed.");
                return null;
            }
        }

        public async Task<List<TraktListItem>> GetHistoryAsync(string uid)
        {
            var arr = await GetAuthedAsync(uid, "/sync/history?extended=full,images&limit=100") as JArray;
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
            // Map the AniSync list tabs onto Trakt: Planning → watchlist, Completed
            // → watched history, Current → in-progress playback (continue-watching).
            // Paused / Dropped / Repeating have no native Trakt surface, so they're
            // backed by the AniSync-managed personal lists.
            List<TraktListItem> items;
            if (list == ListType.Planning)
            {
                items = await GetWatchlistAsync(uid);
            }
            else if (list == ListType.Current)
            {
                items = await GetPlaybackAsync(uid);
            }
            else if (list == ListType.Completed)
            {
                // Completed = watched history MINUS anything still in progress
                // (in playback). A series you're part-way through has watched
                // episodes in history but an active playback, so it belongs under
                // Watching (Current), not Completed.
                var history = await GetHistoryAsync(uid);
                var inProgress = (await GetPlaybackAsync(uid))
                    .Select(i => i.ImdbId)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                items = history.Where(h => !inProgress.Contains(h.ImdbId)).ToList();
            }
            else if (list == ListType.Paused)
            {
                items = await GetCustomStatusItemsAsync(uid, CustomStatusListName("onhold"));
            }
            else if (list == ListType.Dropped)
            {
                items = await GetCustomStatusItemsAsync(uid, CustomStatusListName("dropped"));
            }
            else
            {
                // Repeating has no Trakt surface for video (no Rewatching list).
                items = new List<TraktListItem>();
            }

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

        public Task<bool> PauseScrobbleAsync(string uid, string type, string imdbId, int? season, int? episode, double progress) =>
            string.IsNullOrEmpty(imdbId) || progress <= 0 || progress >= 100
                ? Task.FromResult(false)
                : PostAuthedAsync(uid, "/scrobble/pause", ScrobbleBody(type, imdbId, season, episode, progress));

        public Task<bool> StopScrobbleAsync(string uid, string type, string imdbId, int? season, int? episode, double progress) =>
            string.IsNullOrEmpty(imdbId) || progress <= 0
                ? Task.FromResult(false)
                : PostAuthedAsync(uid, "/scrobble/stop", ScrobbleBody(type, imdbId, season, episode, Math.Min(progress, 100)));

        public async Task<List<TraktListItem>> GetDiscoveryAsync(string uid, string type, string mode, string genre, int page, int limit)
        {
            if (!IsConfigured) return new();
            var traktType = type == "movie" ? "movies" : "shows";

            var path = mode switch
            {
                "trending"    => $"/{traktType}/trending",
                "popular"     => $"/{traktType}/popular",
                "anticipated" => $"/{traktType}/anticipated",
                "watched"     => $"/{traktType}/watched/weekly",
                "recommended" => $"/recommendations/{traktType}",
                _             => null,
            };
            if (path == null) return new();

            var qs = new List<string> { "extended=full,images", $"page={Math.Max(1, page)}", $"limit={limit}" };
            var slug = TraktGenreSlug(genre);
            if (!string.IsNullOrEmpty(slug)) qs.Add($"genres={Uri.EscapeDataString(slug)}");
            if (mode == "recommended") qs.Add("ignore_collected=true");
            var url = $"{path}?{string.Join("&", qs)}";

            JToken json;
            if (mode == "recommended")
            {
                // Personalized — requires the user's token.
                if (string.IsNullOrEmpty(uid)) return new();
                json = await GetAuthedAsync(uid, url);
            }
            else
            {
                json = await GetPublicAsync(url);
            }

            if (json is not JArray arr) return new();

            var items = new List<TraktListItem>();
            foreach (var it in arr)
            {
                // trending/anticipated/watched wrap the title under movie/show;
                // recommendations return the bare object.
                var node = it["movie"] ?? it["show"] ?? it;
                var item = type == "movie" ? MovieItem(node) : ShowItem(node);
                if (!string.IsNullOrEmpty(item.ImdbId)) items.Add(item);
            }
            return items;
        }

        // ── Rich detail-page data (public — api-key only) ───────────────────
        // Trakt has the text/metadata the detail page wants; Cinemeta keeps the
        // images + episodes (Trakt's API ships no images).

        public async Task<TraktVideoSummary> GetSummaryAsync(string type, string imdbId)
        {
            if (!IsConfigured || string.IsNullOrEmpty(imdbId)) return null;
            var t = type == "movie" ? "movies" : "shows";
            if (await GetPublicAsync($"/{t}/{imdbId}?extended=full,images") is not JObject o) return null;
            return new TraktVideoSummary
            {
                Title = (string)o["title"],
                Year = (int?)o["year"],
                Overview = (string)o["overview"],
                Runtime = (int?)o["runtime"],
                Certification = (string)o["certification"],
                Rating = (double?)o["rating"],
                Trailer = (string)o["trailer"],
                Poster = TraktImage(o, "poster"),
                Background = TraktImage(o, "fanart"),
                Genres = (o["genres"] as JArray)?.Select(g => (string)g)
                    .Where(s => !string.IsNullOrEmpty(s)).ToList() ?? new(),
            };
        }

        public async Task<List<TraktCastMember>> GetCastAsync(string type, string imdbId, int limit)
        {
            if (!IsConfigured || string.IsNullOrEmpty(imdbId)) return new();
            var t = type == "movie" ? "movies" : "shows";
            // extended=images surfaces person.images.headshots (Trakt re-added image
            // URLs to the API in 2024); without it the people payload has no images.
            if (await GetPublicAsync($"/{t}/{imdbId}/people?extended=images") is not JObject o
                || o["cast"] is not JArray cast)
                return new();
            var list = new List<TraktCastMember>();
            foreach (var c in cast)
            {
                var name = (string)c["person"]?["name"];
                if (string.IsNullOrEmpty(name)) continue;
                // Newer API: characters[]; older: character (string).
                var character = c["characters"] is JArray chars && chars.Count > 0
                    ? (string)chars[0]
                    : (string)c["character"];
                list.Add(new TraktCastMember
                {
                    Name = name,
                    Character = character,
                    // Trakt's person image key is singular ("headshot", matching
                    // poster / fanart); fall back to the plural just in case.
                    Image = TraktImage(c["person"], "headshot")
                            ?? TraktImage(c["person"], "headshots"),
                    Slug = (string)c["person"]?["ids"]?["slug"],
                });
                if (list.Count >= limit) break;
            }
            return list;
        }

        // Trakt image URLs come back scheme-less (e.g. "media.trakt.tv/images/...");
        // prepend https:// so they're usable as-is in <img src>.
        private static string NormalizeTraktImage(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
            return "https://" + url.TrimStart('/');
        }

        public async Task<List<TraktListItem>> GetRelatedAsync(string type, string imdbId, int limit)
        {
            if (!IsConfigured || string.IsNullOrEmpty(imdbId)) return new();
            var t = type == "movie" ? "movies" : "shows";
            if (await GetPublicAsync($"/{t}/{imdbId}/related?extended=full,images&limit={Math.Max(1, limit)}") is not JArray arr)
                return new();
            var items = new List<TraktListItem>();
            foreach (var it in arr)
            {
                var item = type == "movie" ? MovieItem(it) : ShowItem(it);
                if (!string.IsNullOrEmpty(item.ImdbId)) items.Add(item);
            }
            return items;
        }

        // Full episode list for a series, built from Trakt's seasons endpoint
        // (extended=episodes,full → title / overview / first_aired per episode).
        // Trakt ships no episode images, so the caller merges Cinemeta thumbnails
        // by season+episode. Season 0 (specials) is skipped — the detail view
        // folds season 0 into season 1, which would misgroup real specials.
        public async Task<List<Video>> GetEpisodesAsync(string imdbId)
        {
            var episodes = new List<Video>();
            if (!IsConfigured || string.IsNullOrEmpty(imdbId)) return episodes;
            if (await GetPublicAsync($"/shows/{imdbId}/seasons?extended=episodes,full,images") is not JArray seasons)
                return episodes;

            foreach (var s in seasons)
            {
                var seasonNum = (int?)s["number"] ?? -1;
                if (seasonNum < 1 || s["episodes"] is not JArray eps) continue;
                foreach (var e in eps)
                {
                    if ((int?)e["number"] is not int epNum) continue;
                    var aired = (string)e["first_aired"];
                    var title = (string)e["title"];
                    episodes.Add(new Video
                    {
                        id = $"{imdbId}:{seasonNum}:{epNum}",
                        season = seasonNum,
                        episode = epNum,
                        title = title,
                        name = title,
                        overview = (string)e["overview"],
                        released = aired,
                        firstAired = aired,
                        thumbnail = TraktImage(e, "screenshot"),
                    });
                }
            }
            return episodes;
        }

        // Resolves a TMDB person id to a Trakt person slug (the /discover/actors
        // directory is TMDB-sourced, but the filmography keys off Trakt). null
        // when Trakt has no matching person. Public.
        public async Task<string> ResolveSlugByTmdbAsync(int tmdbId)
        {
            if (!IsConfigured || tmdbId <= 0) return null;
            if (await GetPublicAsync($"/search/tmdb/{tmdbId}?type=person") is not JArray arr) return null;
            foreach (var hit in arr)
            {
                var slug = (string)hit["person"]?["ids"]?["slug"];
                if (!string.IsNullOrEmpty(slug)) return slug;
            }
            return null;
        }

        // Actor filmography for /discover/actor/{slug}: the person's name +
        // headshot plus their movie & show cast credits (deduped by imdb id,
        // newest first). Public — works for anonymous viewers.
        public async Task<(string Name, string Image, List<TraktListItem> Items)> GetPersonCreditsAsync(string slug)
        {
            if (!IsConfigured || string.IsNullOrEmpty(slug)) return (null, null, new());
            var enc = Uri.EscapeDataString(slug);
            var personTask = GetPublicAsync($"/people/{enc}?extended=images");
            var moviesTask = GetPublicAsync($"/people/{enc}/movies?extended=full,images");
            var showsTask = GetPublicAsync($"/people/{enc}/shows?extended=full,images");
            await Task.WhenAll(personTask, moviesTask, showsTask);

            string name = null, image = null;
            if (personTask.Result is JObject p)
            {
                name = (string)p["name"];
                image = TraktImage(p, "headshot") ?? TraktImage(p, "headshots");
            }

            var items = new List<TraktListItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCast(JToken json, string mediaKey, string type)
            {
                if (json is not JObject o || o["cast"] is not JArray cast) return;
                foreach (var c in cast)
                {
                    var item = type == "movie" ? MovieItem(c[mediaKey]) : ShowItem(c[mediaKey]);
                    if (string.IsNullOrEmpty(item.ImdbId)) continue;
                    if (!seen.Add($"{item.Type}:{item.ImdbId}")) continue;
                    items.Add(item);
                }
            }

            AddCast(moviesTask.Result, "movie", "movie");
            AddCast(showsTask.Result, "show", "series");

            items = items.OrderByDescending(i => i.Year ?? 0).ToList();
            return (name, image, items);
        }

        // Maps a Cinemeta genre name to a Trakt genre slug so the existing video
        // genre dropdown can filter the Trakt feeds. Most are just lowercased;
        // Cinemeta's "Sci-Fi" is Trakt's "science-fiction".
        private static string TraktGenreSlug(string cinemetaGenre)
        {
            if (string.IsNullOrWhiteSpace(cinemetaGenre)) return null;
            var g = cinemetaGenre.Trim();
            return g == "Sci-Fi" ? "science-fiction" : g.ToLowerInvariant();
        }

        public async Task<TraktVideoEntry> GetVideoEntryAsync(string uid, string type, string imdbId)
        {
            var entry = new TraktVideoEntry();
            if (string.IsNullOrEmpty(imdbId)) return entry;

            // Watchlist membership.
            var watchlist = await GetWatchlistAsync(uid);
            entry.InWatchlist = watchlist.Any(i =>
                i.Type == type && string.Equals(i.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase));

            // Watched history — a movie is all-or-nothing; a show carries a
            // seasons→episodes tree we count to get progress.
            if (type == "movie")
            {
                var arr = await GetAuthedAsync(uid, "/sync/watched/movies") as JArray;
                entry.Watched = arr?.Any(it =>
                    string.Equals((string)it["movie"]?["ids"]?["imdb"], imdbId, StringComparison.OrdinalIgnoreCase)) == true;

                // A movie has no episode count to derive "watching" from — the
                // only in-progress signal is a paused playback (left part-way).
                if (!entry.Watched)
                {
                    var playback = await GetPlaybackAsync(uid);
                    entry.InPlayback = playback.Any(i =>
                        i.Type == "movie" && string.Equals(i.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                var arr = await GetAuthedAsync(uid, "/sync/watched/shows") as JArray;
                var show = arr?.FirstOrDefault(it =>
                    string.Equals((string)it["show"]?["ids"]?["imdb"], imdbId, StringComparison.OrdinalIgnoreCase));
                if (show?["seasons"] is JArray seasons)
                {
                    var count = 0;
                    foreach (var s in seasons)
                        if (s["episodes"] is JArray eps) count += eps.Count;
                    entry.WatchedEpisodes = count;
                }

                // In-progress signal: a series with an active episode playback is
                // "watching" (continue-watching), the same way a paused movie is.
                // Lets the detail page distinguish watching from completed without
                // relying on a (cross-source, often-mismatched) episode total.
                var playback = await GetPlaybackAsync(uid);
                entry.InPlayback = playback.Any(i =>
                    i.Type == "series" && string.Equals(i.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase));
            }

            // Custom-status lists (On Hold / Dropped / Rewatching) — Trakt has no
            // native surface for these, so check the AniSync personal lists.
            entry.CustomStatus = await ResolveCustomStatusAsync(uid, type, imdbId);

            // User rating.
            var ratingsArr = await GetAuthedAsync(uid, type == "movie" ? "/sync/ratings/movies" : "/sync/ratings/shows") as JArray;
            var key = type == "movie" ? "movie" : "show";
            var rated = ratingsArr?.FirstOrDefault(it =>
                string.Equals((string)it[key]?["ids"]?["imdb"], imdbId, StringComparison.OrdinalIgnoreCase));
            entry.Rating = (int?)rated?["rating"];

            return entry;
        }

        public async Task<bool> SaveVideoEntryAsync(string uid, string type, string imdbId, string status,
            IReadOnlyList<(int Season, int Episode)> watchedEpisodes, int? rating,
            (int Season, int Episode)? inProgress = null)
        {
            if (string.IsNullOrEmpty(imdbId)) return false;
            var token = await GetValidTokenAsync(uid);
            if (token == null) return false;

            // Rating is independent of status — set it (clamped to Trakt's 1-10)
            // when provided, otherwise clear any existing rating.
            if (rating is int r && r > 0)
                await PostAuthedAsync(uid, "/sync/ratings", RatingBody(type, imdbId, Math.Clamp(r, 1, 10)));
            else
                await PostAuthedAsync(uid, "/sync/ratings/remove", SyncBody(type, imdbId, null, null));

            // Each branch makes the target status exclusive — it removes the
            // title from the OTHER Trakt surfaces (watchlist / playback /
            // history) so a movie/series never shows under two lists at once
            // (e.g. Completed AND Watching after a status change).
            switch ((status ?? string.Empty).ToLowerInvariant())
            {
                case "planning":
                    // Only on the watchlist — clear history + any in-progress playback.
                    await ClearPlaybackAsync(uid, type, imdbId);
                    await PostAuthedAsync(uid, "/sync/history/remove", SyncBody(type, imdbId, null, null));
                    await ClearCustomStatusAsync(uid, type, imdbId, null);
                    await AddToWatchlistAsync(uid, type, imdbId);
                    break;

                case "watching":
                    await RemoveFromWatchlistAsync(uid, type, imdbId);
                    await ClearCustomStatusAsync(uid, type, imdbId, null);
                    if (type == "movie")
                    {
                        // Drop any completed record, then ensure an in-progress
                        // playback (a movie's only "watching" signal). Seed at
                        // the midpoint only when none exists, so a real resume
                        // position the user already has isn't reset.
                        await PostAuthedAsync(uid, "/sync/history/remove", SyncBody("movie", imdbId, null, null));
                        if (await FindMoviePlaybackIdAsync(uid, imdbId) == null)
                            await PauseScrobbleAsync(uid, "movie", imdbId, null, null, 50);
                    }
                    else
                    {
                        // Reset history to exactly the watched prefix (1..N):
                        // wipe the show's history, then re-add the prefix. Makes
                        // progress exact rather than additive, so lowering it
                        // (or coming down from Completed) really un-watches the rest.
                        await PostAuthedAsync(uid, "/sync/history/remove", SyncBody("series", imdbId, null, null));
                        if (watchedEpisodes is { Count: > 0 })
                            await PostAuthedAsync(uid, "/sync/history", HistoryEpisodesBody(imdbId, watchedEpisodes));

                        // Seed an in-progress episode playback so the show reads as
                        // "watching" (continue-watching) rather than completed — the
                        // series analogue of the movie midpoint-seed above. Reset to
                        // exactly one in-progress entry on the supplied episode.
                        await ClearShowPlaybackAsync(uid, imdbId);
                        if (inProgress is { } ip)
                            await PauseScrobbleAsync(uid, "series", imdbId, ip.Season, ip.Episode, 50);
                    }
                    break;

                case "completed":
                    await RemoveFromWatchlistAsync(uid, type, imdbId);
                    await ClearPlaybackAsync(uid, type, imdbId);   // no longer in progress
                    await ClearCustomStatusAsync(uid, type, imdbId, null);
                    if (type == "movie")
                        await PostAuthedAsync(uid, "/sync/history", SyncBody("movie", imdbId, null, null));
                    else if (watchedEpisodes is { Count: > 0 })
                        await PostAuthedAsync(uid, "/sync/history", HistoryEpisodesBody(imdbId, watchedEpisodes));
                    break;

                case "onhold":
                case "dropped":
                    // No native Trakt surface — clear them all and park the title
                    // in the matching AniSync personal list (created on demand).
                    await RemoveFromWatchlistAsync(uid, type, imdbId);
                    await ClearPlaybackAsync(uid, type, imdbId);
                    await PostAuthedAsync(uid, "/sync/history/remove", SyncBody(type, imdbId, null, null));
                    var statusKey = (status ?? string.Empty).ToLowerInvariant();
                    await ClearCustomStatusAsync(uid, type, imdbId, statusKey);
                    var listId = await EnsureListIdAsync(uid, CustomStatusListName(statusKey));
                    // Surface a failure (couldn't create/find the personal list, or
                    // the add failed) instead of a silent fake-success, so the modal
                    // tells the user rather than appearing to save.
                    if (listId == null) return false;
                    return await AddToListAsync(uid, listId.Value, type, imdbId);

                default: // "" → None: remove from every surface.
                    await ClearPlaybackAsync(uid, type, imdbId);
                    await PostAuthedAsync(uid, "/sync/history/remove", SyncBody(type, imdbId, null, null));
                    await RemoveFromWatchlistAsync(uid, type, imdbId);
                    await ClearCustomStatusAsync(uid, type, imdbId, null);
                    break;
            }
            return true;
        }

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
            // Public discovery endpoints (trending / anticipated / watched) need
            // only the api-key — accessToken is null for those.
            if (!string.IsNullOrEmpty(accessToken))
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

        // GET against a public Trakt endpoint (no user token — api-key only).
        // Used by the discovery feeds (trending / anticipated / watched) which
        // work for anonymous / non-connected users.
        private async Task<JToken> GetPublicAsync(string path)
        {
            if (!IsConfigured) return null;
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var req = BuildApiRequest(HttpMethod.Get, path, accessToken: null);
                var resp = await client.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trakt public GET {Path} returned {Status}.", path, (int)resp.StatusCode);
                    return null;
                }
                return JToken.Parse(await resp.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt public GET {Path} failed.", path);
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

        // POST that returns the parsed response body (e.g. to read a freshly
        // created list's id), rather than just success/failure.
        private async Task<JToken> PostAuthedReturningAsync(string uid, string path, JObject body)
        {
            var token = await GetValidTokenAsync(uid);
            if (token == null) return null;
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var req = BuildApiRequest(HttpMethod.Post, path, token.access_token, body);
                var resp = await client.SendAsync(req);
                var b = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trakt POST {Path} returned {Status}: {Body}", path, (int)resp.StatusCode, Truncate(b, 300));
                    return null;
                }
                return string.IsNullOrEmpty(b) ? null : JToken.Parse(b);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt POST {Path} failed.", path);
                return null;
            }
        }

        private async Task<bool> DeleteAuthedAsync(string uid, string path)
        {
            var token = await GetValidTokenAsync(uid);
            if (token == null) return false;
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var req = BuildApiRequest(HttpMethod.Delete, path, token.access_token);
                var resp = await client.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trakt DELETE {Path} returned {Status}.", path, (int)resp.StatusCode);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trakt DELETE {Path} failed.", path);
                return false;
            }
        }

        // Trakt playback ("continue watching") id for a movie, or null when the
        // movie has no in-progress playback. Needed because /sync/playback/:id
        // deletion keys off the playback id, not the movie id.
        private async Task<long?> FindMoviePlaybackIdAsync(string uid, string imdbId)
        {
            var arr = await GetAuthedAsync(uid, "/sync/playback?extended=full,images") as JArray;
            if (arr == null) return null;
            foreach (var it in arr)
            {
                if ((string)it["type"] != "movie") continue;
                if (string.Equals((string)it["movie"]?["ids"]?["imdb"], imdbId, StringComparison.OrdinalIgnoreCase))
                    return (long?)it["id"];
            }
            return null;
        }

        // Drops a movie's in-progress playback so it stops reading as "watching".
        private async Task ClearMoviePlaybackAsync(string uid, string imdbId)
        {
            var id = await FindMoviePlaybackIdAsync(uid, imdbId);
            if (id != null) await DeleteAuthedAsync(uid, $"/sync/playback/{id}");
        }

        // Every in-progress episode playback id for a show (a show can have one
        // per episode the user left part-way).
        private async Task<List<long>> FindShowPlaybackIdsAsync(string uid, string imdbId)
        {
            var ids = new List<long>();
            var arr = await GetAuthedAsync(uid, "/sync/playback?extended=full,images") as JArray;
            if (arr == null) return ids;
            foreach (var it in arr)
            {
                if ((string)it["type"] != "episode") continue;
                if (!string.Equals((string)it["show"]?["ids"]?["imdb"], imdbId, StringComparison.OrdinalIgnoreCase)) continue;
                if ((long?)it["id"] is long id) ids.Add(id);
            }
            return ids;
        }

        // Drops a show's in-progress episode playback(s) so it stops reading as "watching".
        private async Task ClearShowPlaybackAsync(string uid, string imdbId)
        {
            foreach (var id in await FindShowPlaybackIdsAsync(uid, imdbId))
                await DeleteAuthedAsync(uid, $"/sync/playback/{id}");
        }

        // Clears in-progress playback for either media type.
        private Task ClearPlaybackAsync(string uid, string type, string imdbId) =>
            type == "movie" ? ClearMoviePlaybackAsync(uid, imdbId) : ClearShowPlaybackAsync(uid, imdbId);

        // ── Custom-status Trakt personal lists ──────────────────────────────
        // Trakt has native surfaces only for planning (watchlist), watching
        // (playback) and completed (history). The remaining AniSync statuses are
        // backed by per-user personal lists so they round-trip to the user's
        // Trakt account (and show up in Trakt apps too).
        // Only two — a free Trakt account caps the number of custom lists, so we
        // don't offer Rewatching for video (it'd need a third list).
        private static readonly (string Status, string Name)[] CustomStatusLists =
        {
            ("onhold",  "On Hold"),
            ("dropped", "Dropped"),
        };

        // status → list name (null when the status isn't custom-list backed).
        private static string CustomStatusListName(string status) =>
            CustomStatusLists.FirstOrDefault(l => l.Status == status).Name;

        // (uid|name) → Trakt list id. List ids are stable, so cache them to skip
        // the /users/me/lists round-trip on subsequent reads/writes.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _listIdCache = new();

        // Finds the user's personal list id by name (cached). Does NOT create it.
        private async Task<long?> FindListIdAsync(string uid, string name)
        {
            if (_listIdCache.TryGetValue(uid + "|" + name, out var cached)) return cached;
            if (await GetAuthedAsync(uid, "/users/me/lists") is not JArray arr) return null;
            foreach (var it in arr)
            {
                if (!string.Equals((string)it["name"], name, StringComparison.OrdinalIgnoreCase)) continue;
                if ((long?)it["ids"]?["trakt"] is long id)
                {
                    _listIdCache[uid + "|" + name] = id;
                    return id;
                }
            }
            return null;
        }

        // Find-or-create the named private personal list.
        private async Task<long?> EnsureListIdAsync(string uid, string name)
        {
            var existing = await FindListIdAsync(uid, name);
            if (existing != null) return existing;

            // Read the new list's id straight from the create response — a
            // follow-up /users/me/lists fetch can race (the list may not show up
            // immediately), which left the item un-added.
            var created = await PostAuthedReturningAsync(uid, "/users/me/lists", new JObject
            {
                ["name"] = name,
                ["description"] = "Managed by AniSync.",
                ["privacy"] = "private",
            });
            if ((long?)created?["ids"]?["trakt"] is long id)
            {
                _listIdCache[uid + "|" + name] = id;
                return id;
            }
            // Fallback (no body returned) — re-resolve.
            return await FindListIdAsync(uid, name);
        }

        private Task<bool> AddToListAsync(string uid, long listId, string type, string imdbId) =>
            PostAuthedAsync(uid, $"/users/me/lists/{listId}/items", SyncBody(type, imdbId, null, null));

        private Task<bool> RemoveFromListAsync(string uid, long listId, string type, string imdbId) =>
            PostAuthedAsync(uid, $"/users/me/lists/{listId}/items/remove", SyncBody(type, imdbId, null, null));

        // Items of the named custom-status list (empty when the list doesn't exist).
        private async Task<List<TraktListItem>> GetCustomStatusItemsAsync(string uid, string name)
        {
            var id = await FindListIdAsync(uid, name);
            if (id == null) return new();
            if (await GetAuthedAsync(uid, $"/users/me/lists/{id}/items?extended=full,images") is not JArray arr) return new();
            var items = new List<TraktListItem>();
            foreach (var it in arr)
            {
                var t = (string)it["type"];
                if (t == "movie") items.Add(MovieItem(it["movie"]));
                else if (t == "show") items.Add(ShowItem(it["show"]));
            }
            return items.Where(i => !string.IsNullOrEmpty(i.ImdbId)).ToList();
        }

        // Removes the title from every custom-status list except the target one
        // (find-only — a list that was never created is simply skipped).
        private async Task ClearCustomStatusAsync(string uid, string type, string imdbId, string exceptStatus)
        {
            foreach (var (st, name) in CustomStatusLists)
            {
                if (st == exceptStatus) continue;
                var id = await FindListIdAsync(uid, name);
                if (id != null) await RemoveFromListAsync(uid, id.Value, type, imdbId);
            }
        }

        // Which custom-status list (if any) currently holds the title.
        private async Task<string> ResolveCustomStatusAsync(string uid, string type, string imdbId)
        {
            var checks = CustomStatusLists.Select(async l =>
            {
                var items = await GetCustomStatusItemsAsync(uid, l.Name);
                return items.Any(i => i.Type == type && string.Equals(i.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase))
                    ? l.Status : null;
            });
            var results = await Task.WhenAll(checks);
            return results.FirstOrDefault(s => s != null);
        }

        private static TraktListItem MovieItem(JToken m) => new()
        {
            Type = "movie",
            ImdbId = (string)m?["ids"]?["imdb"],
            Title = (string)m?["title"],
            Year = (int?)m?["year"],
            Poster = TraktImage(m, "poster"),
            Background = TraktImage(m, "fanart"),
            Rating = (double?)m?["rating"],
        };

        private static TraktListItem ShowItem(JToken s) => new()
        {
            Type = "series",
            ImdbId = (string)s?["ids"]?["imdb"],
            Title = (string)s?["title"],
            Year = (int?)s?["year"],
            Poster = TraktImage(s, "poster"),
            Background = TraktImage(s, "fanart"),
            Rating = (double?)s?["rating"],
        };

        // Pulls the first image of a given kind (poster / fanart / headshots /
        // screenshot / logo) off a node's images object, https-prefixed. null
        // when the node has no images (i.e. extended=images wasn't requested or
        // Trakt has none).
        private static string TraktImage(JToken node, string kind)
            => NormalizeTraktImage((string)(node?["images"]?[kind] as JArray)?.FirstOrDefault());

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

        // Body for the /scrobble/* endpoints — distinct from SyncBody's grouped
        // movies/shows arrays. Scrobble wants a SINGULAR movie/show plus a
        // sibling episode object (season+number) and a top-level progress
        // percentage (0-100). Trakt records the progress so the title shows up
        // in /sync/playback (Continue Watching).
        private static JObject ScrobbleBody(string type, string imdbId, int? season, int? episode, double progress)
        {
            var ids = new JObject { ["imdb"] = imdbId };
            var body = new JObject { ["progress"] = Math.Round(progress, 2) };
            if (type == "movie")
            {
                body["movie"] = new JObject { ["ids"] = ids };
            }
            else
            {
                body["show"] = new JObject { ["ids"] = ids };
                if (episode.HasValue)
                {
                    // Default to season 1 when the caller didn't tag a cour —
                    // single-season series carry a null season but a real
                    // episode number, and Trakt needs both to address it.
                    body["episode"] = new JObject
                    {
                        ["season"] = season ?? 1,
                        ["number"] = episode.Value,
                    };
                }
            }
            return body;
        }

        // /sync/history body marking specific episodes watched: shows → seasons
        // → episodes, grouped by season number. Used by SaveVideoEntryAsync to
        // mark "watched up to N" for a series.
        private static JObject HistoryEpisodesBody(string imdbId, IEnumerable<(int Season, int Episode)> eps)
        {
            var seasons = eps
                .GroupBy(e => e.Season)
                .Select(g => new JObject
                {
                    ["number"] = g.Key,
                    ["episodes"] = new JArray(g.Select(e => new JObject { ["number"] = e.Episode })),
                });
            var show = new JObject
            {
                ["ids"] = new JObject { ["imdb"] = imdbId },
                ["seasons"] = new JArray(seasons),
            };
            return new JObject { ["shows"] = new JArray { show } };
        }

        // /sync/ratings body — a singular movie/show id + the 1-10 rating, under
        // the grouped movies/shows array the endpoint expects.
        private static JObject RatingBody(string type, string imdbId, int rating)
        {
            var item = new JObject
            {
                ["ids"] = new JObject { ["imdb"] = imdbId },
                ["rating"] = rating,
            };
            return new JObject { [type == "movie" ? "movies" : "shows"] = new JArray { item } };
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
