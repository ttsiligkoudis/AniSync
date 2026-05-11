using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace AnimeList.Services
{
    /// <summary>
    /// Stateless anonymous AniList client used by cross-service fallbacks.
    /// </summary>
    public class AnilistFallback : IAnilistFallback
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly IMemoryCache _cache;
        private const string _api = "https://graphql.anilist.co";
        private const int PageSize = 50;
        // 24-hour TTL on the seasonal stat counts — the catalog moves slowly
        // (a few additions per week, status flips on Tuesdays, etc.) so day-
        // stale numbers are fine. Keyed by (season, year) so the cache
        // naturally rotates when AniList moves to the next season; the old
        // season's entry just expires unread.
        private static readonly TimeSpan SeasonStatsCacheDuration = TimeSpan.FromHours(24);

        public AnilistFallback(IHttpClientFactory clientFactory, IAnimeMappingService mappingService, IMemoryCache cache)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _cache = cache;
        }

        public async Task<List<Meta>> GetAiringScheduleAsync(AnimeService translateTo, string skip = null, string genre = null)
        {
            // Genre branch: AniList's airingSchedules query has no genre
            // filter, so when the caller wants to slice "airing" by genre we
            // swap to the Media(status: RELEASING, genre: $genre) shape. Same
            // user-facing intent — "show me what's airing in Action" — just
            // sourced from currently-airing anime rather than the upcoming-
            // episode schedule. Results lose per-episode air dates because
            // there's no schedule attached to a status-based fetch; the cards
            // still carry the show's full metadata.
            if (!string.IsNullOrEmpty(genre))
            {
                return await GetCurrentlyAiringByGenreAsync(translateTo, skip, genre);
            }

            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;

            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($page: Int, $perPage: Int) {
                        Page(page: $page, perPage: $perPage) {
                            airingSchedules(notYetAired: true, sort: TIME) {
                                airingAt
                                episode
                                media {
                                    id
                                    format
                                    title { english romaji }
                                    coverImage { large }
                                    description
                                }
                            }
                        }
                    }",
                variables = new { page, perPage = PageSize }
            });

            var data = await PostJsonAsync(requestBody);
            var schedules = data?.Page?.airingSchedules;
            if (schedules == null) return [];

            await _mappingService.EnsureLoadedAsync();

            var seen = new HashSet<string>();
            var result = new List<Meta>();
            foreach (var sched in schedules)
            {
                var media = sched.media;
                if (media == null) continue;

                var anilistId = (int?)media.id;
                if (!anilistId.HasValue) continue;

                var externalId = await ResolveExternalIdAsync(anilistId.Value, translateTo);
                if (!seen.Add(externalId)) continue;

                var name = string.IsNullOrEmpty((string)media.title.english)
                    ? (string)media.title.romaji
                    : (string)media.title.english;

                var airingAt = (long?)sched.airingAt;
                var episode = (int?)sched.episode;
                var description = BuildAiringDescription((string)media.description, episode, airingAt);

                result.Add(new Meta(description)
                {
                    id = externalId,
                    type = IsMovieFormat((string)media.format) ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = name,
                    poster = (string)media.coverImage?.large,
                });
            }

            return result;
        }

        // "Airing + genre" fallback path. Uses Media(status: RELEASING, genre)
        // because airingSchedules can't be sliced per-genre. Sort by popularity
        // so the most-watched currently-airing shows in the genre lead.
        private async Task<List<Meta>> GetCurrentlyAiringByGenreAsync(AnimeService translateTo, string skip, string genre)
        {
            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;

            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($page: Int, $perPage: Int, $genre: String) {
                        Page(page: $page, perPage: $perPage) {
                            media(type: ANIME, status: RELEASING, genre: $genre, sort: POPULARITY_DESC) {
                                id
                                format
                                title { english romaji }
                                coverImage { large }
                                description
                            }
                        }
                    }",
                variables = new { page, perPage = PageSize, genre }
            });

            var data = await PostJsonAsync(requestBody);
            var mediaArr = data?.Page?.media;
            if (mediaArr == null) return [];

            await _mappingService.EnsureLoadedAsync();

            var seen = new HashSet<string>();
            var result = new List<Meta>();
            foreach (var media in mediaArr)
            {
                var anilistId = (int?)media.id;
                if (!anilistId.HasValue) continue;

                var externalId = await ResolveExternalIdAsync(anilistId.Value, translateTo);
                if (!seen.Add(externalId)) continue;

                var name = string.IsNullOrEmpty((string)media.title?.english)
                    ? (string)media.title?.romaji
                    : (string)media.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                result.Add(new Meta((string)media.description)
                {
                    id = externalId,
                    type = IsMovieFormat((string)media.format) ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = name,
                    poster = (string)media.coverImage?.large,
                });
            }

            return result;
        }

        public async Task<List<Link>> GetRecommendationsAsync(int anilistId, AnimeService translateTo)
        {
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            recommendations(sort: RATING_DESC, perPage: 25) {
                                edges {
                                    node {
                                        mediaRecommendation {
                                            id
                                            title { english romaji }
                                        }
                                    }
                                }
                            }
                        }
                    }",
                variables = new { id = anilistId }
            });

            var data = await PostJsonAsync(requestBody);
            var edges = data?.Media?.recommendations?.edges;
            if (edges == null) return [];

            await _mappingService.EnsureLoadedAsync();

            var result = new List<Link>();
            foreach (var edge in edges)
            {
                var rec = edge.node?.mediaRecommendation;
                if (rec == null) continue;

                var recId = (int?)rec.id;
                if (!recId.HasValue) continue;

                var name = string.IsNullOrEmpty((string)rec.title?.english)
                    ? (string)rec.title?.romaji
                    : (string)rec.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                var externalId = await ResolveExternalIdAsync(recId.Value, translateTo);

                // Stremio's Link.url is opened externally, so we point at the AniList page;
                // the externalId is preserved in name for clients that want to deep-link inside
                // the addon. (We can revisit this if a Stremio "go to detail" URL form exists.)
                result.Add(new Link
                {
                    name = name,
                    category = "Similar",
                    url = $"https://anilist.co/anime/{recId.Value}",
                });
            }
            return result;
        }

        // The three public per-anime side-data fetchers (recommendations,
        // relations, supplementary links) all hit the same Media(id) root on
        // AniList. Fetching them separately meant up to three GraphQL
        // round-trips per detail render plus three independent cache entries.
        // FetchSidedataAsync collapses them into one combined query with one
        // cache entry; the three public methods below project off the cached
        // record so the call shape (and any caller logic) stays unchanged.
        private sealed record AnilistSidedata(
            List<Meta> Recommendations,
            List<Meta> Related,
            List<Link> SupplementaryLinks);

        public async Task<List<Meta>> GetRecommendationMetasAsync(int anilistId)
            => (await FetchSidedataAsync(anilistId)).Recommendations;

        private async Task<AnilistSidedata> FetchSidedataAsync(int anilistId)
        {
            var cacheKey = $"anilist:sidedata:{anilistId}";
            if (_cache.TryGetValue<AnilistSidedata>(cacheKey, out var cached) && cached != null)
                return cached;

            var empty = new AnilistSidedata([], [], []);

            try
            {
                // One combined query: recommendations + relations + tags +
                // studios + staff. All five subselections share the same
                // Media(id) root so AniList resolves them in one go; the
                // complexity-budget cost is well within their 500 ceiling.
                var requestBody = SerializeObject(new
                {
                    query = @"
                        query ($id: Int) {
                            Media(id: $id, type: ANIME) {
                                recommendations(sort: RATING_DESC, perPage: 15) {
                                    edges {
                                        node {
                                            mediaRecommendation {
                                                id
                                                format
                                                episodes
                                                averageScore
                                                seasonYear
                                                title { english romaji }
                                                coverImage { large }
                                            }
                                        }
                                    }
                                }
                                relations {
                                    edges {
                                        relationType
                                        node {
                                            id
                                            type
                                            format
                                            episodes
                                            averageScore
                                            seasonYear
                                            title { english romaji }
                                            coverImage { large }
                                        }
                                    }
                                }
                                tags { id name rank isAdult }
                                studios { edges { isMain node { id name siteUrl } } }
                                staff { edges { role node { id name { full } siteUrl } } }
                            }
                        }",
                    variables = new { id = anilistId }
                });

                var data = await PostJsonAsync(requestBody);
                var media = data?.Media;
                if (media == null) return empty;

                var sidedata = new AnilistSidedata(
                    Recommendations: ParseRecommendations(media),
                    Related: ParseRelated(media),
                    SupplementaryLinks: ParseSupplementaryLinks(media));

                // Only cache when at least one bucket is non-empty so a
                // transient AniList blip / rate-limit / 5xx doesn't lock in
                // an empty record for 24h.
                if (sidedata.Recommendations.Count > 0
                    || sidedata.Related.Count > 0
                    || sidedata.SupplementaryLinks.Count > 0)
                {
                    _cache.Set(cacheKey, sidedata, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = SeasonStatsCacheDuration,
                    });
                }
                return sidedata;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] FetchSidedataAsync failed: {ex.Message}");
                return empty;
            }
        }

        private static List<Meta> ParseRecommendations(dynamic media)
        {
            var edges = media?.recommendations?.edges;
            if (edges == null) return [];

            var result = new List<Meta>();
            foreach (var edge in edges)
            {
                var rec = edge.node?.mediaRecommendation;
                if (rec == null) continue;
                var recId = (int?)rec.id;
                if (!recId.HasValue) continue;
                var name = string.IsNullOrEmpty((string)rec.title?.english)
                    ? (string)rec.title?.romaji
                    : (string)rec.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                result.Add(new Meta
                {
                    id = $"{anilistPrefix}{recId.Value}",
                    name = name,
                    poster = (string)rec.coverImage?.large,
                    type = IsMovieFormat((string)rec.format)
                        ? MetaType.movie.ToString()
                        : MetaType.series.ToString(),
                    score = rec.averageScore != null
                        ? Math.Round((double)rec.averageScore / 10, 1)
                        : (double?)null,
                    episodes = (int?)rec.episodes,
                    year = (int?)rec.seasonYear,
                    format = NormalizeFormat((string)rec.format),
                });
            }
            return result;
        }

        private static List<Meta> ParseRelated(dynamic media)
        {
            var edges = media?.relations?.edges;
            if (edges == null) return [];

            var result = new List<Meta>();
            foreach (var edge in edges)
            {
                var relationType = (string)edge.relationType;
                if (relationType != "PREQUEL" && relationType != "SEQUEL") continue;

                var node = edge.node;
                if (node == null) continue;
                if ((string)node.type != "ANIME") continue;

                var relId = (int?)node.id;
                if (!relId.HasValue) continue;

                var name = string.IsNullOrEmpty((string)node.title?.english)
                    ? (string)node.title?.romaji
                    : (string)node.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                result.Add(new Meta
                {
                    id = $"{anilistPrefix}{relId.Value}",
                    name = name,
                    poster = (string)node.coverImage?.large,
                    type = IsMovieFormat((string)node.format)
                        ? MetaType.movie.ToString()
                        : MetaType.series.ToString(),
                    score = node.averageScore != null
                        ? Math.Round((double)node.averageScore / 10, 1)
                        : (double?)null,
                    episodes = (int?)node.episodes,
                    year = (int?)node.seasonYear,
                    format = NormalizeFormat((string)node.format),
                });
            }

            // Chronological-ish ordering so the carousel reads as a timeline.
            return result.OrderBy(m => m.year ?? int.MaxValue).ToList();
        }

        private static List<Link> ParseSupplementaryLinks(dynamic media)
        {
            var result = new List<Link>();

            if (media?.tags != null)
            {
                foreach (var tag in media.tags)
                {
                    if ((bool?)tag.isAdult == true) continue;
                    var rank = (int?)tag.rank ?? 0;
                    if (rank < 50) continue;
                    var name = (string)tag.name;
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add(new Link
                    {
                        name = name,
                        category = "Tag",
                        url = $"https://anilist.co/search/anime?genres={Uri.EscapeDataString(name)}",
                        anilistId = (long?)tag.id,
                    });
                }
            }

            if (media?.studios?.edges != null)
            {
                foreach (var edge in media.studios.edges)
                {
                    if ((bool?)edge.isMain != true) continue;
                    var name = (string)edge.node?.name;
                    var siteUrl = (string)edge.node?.siteUrl;
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add(new Link
                    {
                        name = name,
                        category = "Studio",
                        url = siteUrl,
                        anilistId = (long?)edge.node?.id,
                    });
                }
            }

            if (media?.staff?.edges != null)
            {
                foreach (var edge in media.staff.edges)
                {
                    var role = (string)edge.role;
                    var name = (string)edge.node?.name?.full;
                    var siteUrl = (string)edge.node?.siteUrl;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(role)) continue;
                    result.Add(new Link
                    {
                        name = name,
                        category = StaffRoleToCategory(role),
                        url = siteUrl,
                        anilistId = (long?)edge.node?.id,
                    });
                }
            }

            return result;
        }

        public async Task<List<StreamingLink>> GetExternalLinksAsync(int anilistId)
        {
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            externalLinks { site url type }
                        }
                    }",
                variables = new { id = anilistId }
            });

            var data = await PostJsonAsync(requestBody);
            var links = data?.Media?.externalLinks;
            if (links == null) return [];

            var result = new List<StreamingLink>();
            foreach (var link in links)
            {
                if ((string)link.type != "STREAMING") continue;
                var site = (string)link.site;
                var url = (string)link.url;
                if (string.IsNullOrEmpty(url)) continue;
                result.Add(new StreamingLink { Site = site, Url = url });
            }
            return result;
        }

        public async Task<string> GetYoutubeTrailerIdAsync(int anilistId)
        {
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            trailer { id site }
                        }
                    }",
                variables = new { id = anilistId }
            });

            var data = await PostJsonAsync(requestBody);
            var trailer = data?.Media?.trailer;
            if (trailer == null) return null;
            // AniList stores YouTube videos as {site: "youtube", id: "<videoId>"}; other
            // hosters (Dailymotion etc.) are skipped because Stremio's Trailer model only
            // accepts a YouTube id.
            if ((string)trailer.site != "youtube") return null;
            return (string)trailer.id;
        }

        /// <summary>
        /// Picks the most stable id for the requested service, in priority order:
        /// IMDb &gt; TMDB &gt; service-native (kitsu/mal/anilist) &gt; anilist fallback.
        /// </summary>
        private async Task<string> ResolveExternalIdAsync(int anilistId, AnimeService translateTo)
        {
            var mapping = await _mappingService.GetAnilistMapping($"{anilistPrefix}{anilistId}");

            if (!string.IsNullOrEmpty(mapping?.ImdbId)) return mapping.ImdbId;
            if (!string.IsNullOrEmpty(mapping?.TmdbId)) return $"{tmdbPrefix}{mapping.TmdbId}";

            if (translateTo == AnimeService.Kitsu && mapping?.KitsuId != null)
                return $"{kitsuPrefix}{mapping.KitsuId}";
            if (translateTo == AnimeService.MyAnimeList && mapping?.MalId != null)
                return $"{malPrefix}{mapping.MalId}";

            return $"{anilistPrefix}{anilistId}";
        }

        /// <summary>
        /// Browse-flavour id resolution — prefers the primary's NATIVE id
        /// (kitsu:N / mal:N / anilist:N) over the cross-service imdb/tmdb
        /// groupers. The browse routes (/discover?tag=, /staff/{id},
        /// /studio/{id}) want each card to hand off to /anime/{id} with the
        /// user's primary's per-cour id so Manage Entry resolves directly
        /// instead of bouncing through an imdb-mapped franchise umbrella.
        /// Falls back to anilist:N when the requested service has no
        /// matching id on the mapping row.
        /// </summary>
        private async Task<string> ResolveNativeIdAsync(int anilistId, AnimeService translateTo)
        {
            // AniList primary (or anonymous, which defaults to Kitsu but
            // could just as well land back on AniList ids) — no lookup
            // needed, we already have the anilist id.
            if (translateTo == AnimeService.Anilist)
                return $"{anilistPrefix}{anilistId}";

            var mapping = await _mappingService.GetAnilistMapping($"{anilistPrefix}{anilistId}");

            if (translateTo == AnimeService.Kitsu && mapping?.KitsuId != null)
                return $"{kitsuPrefix}{mapping.KitsuId}";
            if (translateTo == AnimeService.MyAnimeList && mapping?.MalId != null)
                return $"{malPrefix}{mapping.MalId}";

            // No native mapping → ship the AniList id. The detail-page
            // controller already handles cross-service id resolution, so
            // /anime/anilist:N still renders for a Kitsu user; they just
            // won't be able to Manage Entry on it (which is the correct
            // outcome anyway, since the anime isn't on their service).
            return $"{anilistPrefix}{anilistId}";
        }

        private static string BuildAiringDescription(string raw, int? episode, long? airingAtUnix)
        {
            var prefix = string.Empty;
            if (episode.HasValue && airingAtUnix.HasValue)
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(airingAtUnix.Value).UtcDateTime;
                prefix = $"Episode {episode.Value} airs {when:yyyy-MM-dd HH:mm} UTC.\n\n";
            }
            return prefix + (raw ?? string.Empty);
        }

        private async Task<dynamic> PostJsonAsync(string requestBody)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _api)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            return DeserializeObject<dynamic>(content)?.data;
        }

        public async Task<(int currentlyAiring, int newThisSeason, int totalThisSeason)> GetSeasonStatsAsync()
        {
            // Cache key includes season + year so the entry naturally
            // invalidates when AniList moves to the next season — the
            // OLD entry just sits dead in cache until eviction.
            // AnilistFallback is registered as Scoped, but IMemoryCache is
            // the singleton so the cache survives across requests despite
            // this service's per-request lifetime.
            var (season, year) = GetSeasonAndYear(SeasonCurrent);
            var cacheKey = $"anilist:season-stats:{season}:{year}";

            if (_cache.TryGetValue<(int, int, int)>(cacheKey, out var cached))
            {
                return cached;
            }

            // Earlier attempts derived the three counts from
            // `Page.pageInfo.total` of three separately-filtered Page
            // queries, but AniList's pageInfo.total reliably collapses to
            // the 5000 hard-cap whenever filter args are present (even on
            // a single-Page document). Switching to a list-walk: pull the
            // season's whole media list (~50–200 entries), each carrying
            // its `status`, and bucket client-side. perPage:50 means
            // 1–4 page fetches per call, gated by the 24h cache.
            try
            {
                var stats = await FetchSeasonStatsAsync(season, year);
                if (stats.totalThisSeason > 0)
                {
                    // Only cache successful results so a transient blip
                    // (sandbox blocks GraphQL / rate-limit / 5xx) doesn't
                    // lock in zeros for 24 hours.
                    _cache.Set(cacheKey, stats, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = SeasonStatsCacheDuration,
                    });
                }
                return stats;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetSeasonStatsAsync failed: {ex.Message}");
                return (0, 0, 0);
            }
        }

        private async Task<(int currentlyAiring, int newThisSeason, int totalThisSeason)> FetchSeasonStatsAsync(string season, int year)
        {
            int total = 0;
            int airing = 0;
            int newThis = 0;
            int page = 1;

            while (true)
            {
                var requestBody = SerializeObject(new
                {
                    query = @"
                        query ($page: Int, $season: MediaSeason, $seasonYear: Int) {
                            Page(page: $page, perPage: 50) {
                                pageInfo { hasNextPage }
                                media(season: $season, seasonYear: $seasonYear, type: ANIME) {
                                    status
                                }
                            }
                        }",
                    variables = new { page, season, seasonYear = year }
                });

                var data = await PostJsonAsync(requestBody);
                var mediaArr = data?.Page?.media;
                if (mediaArr == null) return (0, 0, 0);

                foreach (var m in mediaArr)
                {
                    total++;
                    var status = (string)m.status;
                    // RELEASING counts as both "airing now" and "new this season".
                    // NOT_YET_RELEASED counts only as "new this season" (premieres
                    // later in the calendar season). FINISHED/CANCELLED/HIATUS
                    // are still in the season's catalog but neither airing nor
                    // newly-arriving — only counted into the total.
                    if (status == "RELEASING") { airing++; newThis++; }
                    else if (status == "NOT_YET_RELEASED") { newThis++; }
                }

                var hasNext = (bool?)data?.Page?.pageInfo?.hasNextPage ?? false;
                if (!hasNext) break;
                page++;
                if (page > 20) break; // safety: ~1000 entries is well past any real season
            }

            return (airing, newThis, total);
        }

        public async Task<List<Meta>> GetNewEpisodesTodayAsync()
        {
            // Bound the query to [today 00:00 UTC, tomorrow 00:00 UTC) using
            // AniList's airingAt_greater / airingAt_lesser unix-timestamp
            // filters. Yes UTC fuzzes the edges for viewers far from GMT —
            // a JST user just past midnight UTC sees a fresh "today" already
            // populated with their early-morning JST airings — but it keeps
            // the server-side cache key unambiguous and the boundary
            // consistent for every visitor regardless of where they connect
            // from.
            var todayUtc = DateTime.UtcNow.Date;
            var startUnix = new DateTimeOffset(todayUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            var endUnix = new DateTimeOffset(todayUtc.AddDays(1), TimeSpan.Zero).ToUnixTimeSeconds();

            // Key on the UTC date so the entry auto-rotates at midnight; old
            // day's entry just sits dead until eviction. The 'until end of
            // day' expiration below means at most one upstream call per day.
            var cacheKey = $"anilist:new-episodes-today:{todayUtc:yyyy-MM-dd}";

            if (_cache.TryGetValue<List<Meta>>(cacheKey, out var cached) && cached != null)
            {
                return cached;
            }

            try
            {
                var requestBody = SerializeObject(new
                {
                    query = @"
                        query ($startUnix: Int, $endUnix: Int, $page: Int) {
                            Page(page: $page, perPage: 50) {
                                pageInfo { hasNextPage }
                                airingSchedules(airingAt_greater: $startUnix, airingAt_lesser: $endUnix, sort: TIME) {
                                    airingAt
                                    episode
                                    media {
                                        id
                                        format
                                        episodes
                                        averageScore
                                        seasonYear
                                        title { english romaji }
                                        coverImage { large }
                                    }
                                }
                            }
                        }",
                    variables = new { startUnix, endUnix, page = 1 }
                });

                var data = await PostJsonAsync(requestBody);
                var schedules = data?.Page?.airingSchedules;
                if (schedules == null) return [];

                // Multiple schedules can hit on the same anime in one day
                // (cours overlapping, special + main, etc.) — dedupe by
                // anilist id and keep the earliest airing time on the day.
                var seen = new HashSet<int>();
                var result = new List<Meta>();
                foreach (var sched in schedules)
                {
                    var media = sched.media;
                    if (media == null) continue;

                    var anilistId = (int?)media.id;
                    if (!anilistId.HasValue || !seen.Add(anilistId.Value)) continue;

                    var name = string.IsNullOrEmpty((string)media.title?.english)
                        ? (string)media.title?.romaji
                        : (string)media.title?.english;
                    if (string.IsNullOrEmpty(name)) continue;

                    result.Add(new Meta
                    {
                        // Stay in anilist:N space so AnimeController.Detail
                        // can resolve to the user's primary on click — same
                        // pattern as the other dashboard shelves.
                        id = $"{anilistPrefix}{anilistId.Value}",
                        name = name,
                        poster = (string)media.coverImage?.large,
                        type = IsMovieFormat((string)media.format)
                            ? MetaType.movie.ToString()
                            : MetaType.series.ToString(),
                        score = media.averageScore != null
                            ? Math.Round((double)media.averageScore / 10, 1)
                            : (double?)null,
                        episodes = (int?)media.episodes,
                        year = (int?)media.seasonYear,
                        format = NormalizeFormat((string)media.format),
                    });
                }

                if (result.Count > 0)
                {
                    // Pin expiration to the next UTC midnight so the cache
                    // holds for exactly today rather than a rolling 24h
                    // window. A fetch at 23:00 UTC caches for 1h; a fetch
                    // at 01:00 UTC caches for ~23h. Either way, the next
                    // midnight wipes it and a fresh fetch builds tomorrow's
                    // shelf.
                    _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpiration = new DateTimeOffset(todayUtc.AddDays(1), TimeSpan.Zero),
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetNewEpisodesTodayAsync failed: {ex.Message}");
                return [];
            }
        }

        public async Task<List<Meta>> GetRelatedAsync(int anilistId)
            => (await FetchSidedataAsync(anilistId)).Related;

        public async Task<List<Link>> GetSupplementaryLinksAsync(int anilistId)
            => (await FetchSidedataAsync(anilistId)).SupplementaryLinks;

        // Mirrors AnilistService.StaffRoleToCategory — keeps the augmented
        // page's link categories identical to a native AniList load.
        private static string StaffRoleToCategory(string role)
        {
            var r = role.ToLowerInvariant();
            if (r.Contains("director")) return "director";
            if (r.Contains("writ") || r.Contains("script") || r.Contains("creator")) return "writer";
            if (r.Contains("composer") || r.Contains("music")) return "Composer";
            if (r.Contains("character design") || r.Contains("art")) return "Artist";
            if (r.Contains("producer")) return "Producer";
            return "Staff";
        }

        // Shared poster builder for tag / staff / studio browse queries —
        // same shape: each entry has id + format + title + coverImage +
        // averageScore + seasonYear + episodes. Translates ids into the
        // requested service's NATIVE id (kitsu:N / mal:N / anilist:N), not
        // the imdb/tmdb cross-service groupers — clicks should land on the
        // user's primary's per-cour detail page so Manage Entry resolves
        // directly. AniList id is the fallback when no native mapping
        // exists for the requested service.
        private async Task<List<Meta>> BuildBrowseMetasAsync(dynamic mediaArr, AnimeService translateTo)
        {
            if (mediaArr == null) return [];
            await _mappingService.EnsureLoadedAsync();
            var seen = new HashSet<string>();
            var result = new List<Meta>();
            foreach (var media in mediaArr)
            {
                var anilistId = (int?)media.id;
                if (!anilistId.HasValue) continue;

                var externalId = await ResolveNativeIdAsync(anilistId.Value, translateTo);
                if (!seen.Add(externalId)) continue;

                var name = string.IsNullOrEmpty((string)media.title?.english)
                    ? (string)media.title?.romaji
                    : (string)media.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                result.Add(new Meta((string)media.description)
                {
                    id = externalId,
                    type = IsMovieFormat((string)media.format) ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = name,
                    poster = (string)media.coverImage?.large,
                    score = media.averageScore != null
                        ? Math.Round((double)media.averageScore / 10, 1)
                        : (double?)null,
                    episodes = (int?)media.episodes,
                    year = (int?)media.seasonYear,
                    format = NormalizeFormat((string)media.format),
                });
            }
            return result;
        }

        public async Task<List<Meta>> GetByTagAsync(string tag, AnimeService translateTo, string skip = null)
        {
            if (string.IsNullOrWhiteSpace(tag)) return [];
            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;

            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($page: Int, $perPage: Int, $tag: String) {
                        Page(page: $page, perPage: $perPage) {
                            media(type: ANIME, tag: $tag, sort: POPULARITY_DESC) {
                                id
                                format
                                episodes
                                averageScore
                                seasonYear
                                title { english romaji }
                                coverImage { large }
                                description
                            }
                        }
                    }",
                variables = new { page, perPage = PageSize, tag },
            });

            var data = await PostJsonAsync(requestBody);
            return await BuildBrowseMetasAsync(data?.Page?.media, translateTo);
        }

        public async Task<(string Name, List<Meta> Items)> GetStaffMediaAsync(int staffId, AnimeService translateTo, string skip = null)
        {
            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;

            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int, $page: Int, $perPage: Int) {
                        Staff(id: $id) {
                            name { full }
                            staffMedia(type: ANIME, sort: POPULARITY_DESC, page: $page, perPage: $perPage) {
                                edges {
                                    node {
                                        id
                                        format
                                        episodes
                                        averageScore
                                        seasonYear
                                        title { english romaji }
                                        coverImage { large }
                                        description
                                    }
                                }
                            }
                        }
                    }",
                variables = new { id = staffId, page, perPage = PageSize },
            });

            var data = await PostJsonAsync(requestBody);
            var staff = data?.Staff;
            if (staff == null) return (null, []);

            var name = (string)staff.name?.full;
            // Flatten edges → node array so BuildBrowseMetasAsync can reuse
            // the same shape it uses for the tag / studio paths.
            var nodes = new List<dynamic>();
            if (staff.staffMedia?.edges != null)
            {
                foreach (var edge in staff.staffMedia.edges)
                    if (edge.node != null) nodes.Add(edge.node);
            }
            return (name, await BuildBrowseMetasAsync(nodes, translateTo));
        }

        public async Task<(string Name, List<Meta> Items)> GetStudioMediaAsync(int studioId, AnimeService translateTo, string skip = null)
        {
            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;

            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int, $page: Int, $perPage: Int) {
                        Studio(id: $id) {
                            name
                            media(sort: POPULARITY_DESC, page: $page, perPage: $perPage) {
                                edges {
                                    node {
                                        id
                                        type
                                        format
                                        episodes
                                        averageScore
                                        seasonYear
                                        title { english romaji }
                                        coverImage { large }
                                        description
                                    }
                                }
                            }
                        }
                    }",
                variables = new { id = studioId, page, perPage = PageSize },
            });

            var data = await PostJsonAsync(requestBody);
            var studio = data?.Studio;
            if (studio == null) return (null, []);

            var name = (string)studio.name;
            var nodes = new List<dynamic>();
            if (studio.media?.edges != null)
            {
                foreach (var edge in studio.media.edges)
                {
                    if (edge.node == null) continue;
                    // Studio.media returns Manga + Anime mixed. Filter to
                    // anime-only on the client since the GraphQL Studio.media
                    // arg list doesn't expose a type filter directly.
                    if ((string)edge.node.type != "ANIME") continue;
                    nodes.Add(edge.node);
                }
            }
            return (name, await BuildBrowseMetasAsync(nodes, translateTo));
        }
    }
}
