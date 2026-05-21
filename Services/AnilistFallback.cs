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
                                            format
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

                // Stremio-side "Similar" chip — points at web.stremio.com
                // so a chip tap opens the recommended anime inside Stremio
                // (or the native app via Universal / App Links) instead of
                // bouncing to anilist.co in a browser. The id format
                // mirrors the Sequel / Prequel relation chips: anilist:N
                // resolved by whichever Stremio addon catalogs anilist
                // ids on the user's side.
                var recIsMovie = IsMovieFormat((string)rec.format);
                var recStremioType = recIsMovie ? MetaType.movie.ToString() : MetaType.series.ToString();
                var encodedId = Uri.EscapeDataString($"{anilistPrefix}{recId.Value}");
                result.Add(new Link
                {
                    name = name,
                    category = "Similar",
                    url = $"https://web.stremio.com/#/detail/{recStremioType}/{encodedId}",
                    anilistId = recId.Value,
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
            List<Link> SupplementaryLinks,
            List<Link> RelatedLinks);

        public async Task<List<Meta>> GetRecommendationMetasAsync(int anilistId, AnimeService translateTo = AnimeService.Anilist, bool groupSeasons = false)
            => await TranslateMetaIdsAsync(
                CloneMetas((await FetchSidedataAsync(anilistId)).Recommendations),
                translateTo, groupSeasons);

        private async Task<AnilistSidedata> FetchSidedataAsync(int anilistId)
        {
            var cacheKey = $"anilist:sidedata:{anilistId}";
            if (_cache.TryGetValue<AnilistSidedata>(cacheKey, out var cached) && cached != null)
                return cached;

            var empty = new AnilistSidedata([], [], [], []);

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
                                            isAdult
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
                    SupplementaryLinks: ParseSupplementaryLinks(media),
                    RelatedLinks: ParseRelatedLinks(media));

                // Only cache when at least one bucket is non-empty so a
                // transient AniList blip / rate-limit / 5xx doesn't lock in
                // an empty record for 24h.
                if (sidedata.Recommendations.Count > 0
                    || sidedata.Related.Count > 0
                    || sidedata.SupplementaryLinks.Count > 0
                    || sidedata.RelatedLinks.Count > 0)
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

        /// <summary>
        /// Stremio-shape relation Links — Sequel / Prequel labels with
        /// web.stremio.com deep-link URLs. Mirrors the inline relation
        /// builder in <see cref="AnilistService"/> so the imdb-grouped
        /// enrichment in <see cref="MetaController"/> can inject the same
        /// chip strip the per-service paths emit natively. The Meta-
        /// returning <see cref="ParseRelated"/> collapses Sequel and
        /// Prequel into a single "Related" carousel for the web app's
        /// detail page — Stremio needs them tagged.
        /// </summary>
        private static List<Link> ParseRelatedLinks(dynamic media)
        {
            var edges = media?.relations?.edges;
            if (edges == null) return [];

            var relLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PREQUEL"] = "Prequel",
                ["SEQUEL"]  = "Sequel",
            };

            var result = new List<Link>();
            foreach (var edge in edges)
            {
                var relType = (string)edge.relationType;
                if (relType == null || !relLabels.TryGetValue(relType, out var label)) continue;

                var node = edge.node;
                if (node == null) continue;
                if ((bool?)node.isAdult == true) continue;
                if (!string.Equals((string)node.type, "ANIME", StringComparison.OrdinalIgnoreCase)) continue;

                var relId = (int?)node.id;
                if (!relId.HasValue) continue;

                var name = string.IsNullOrEmpty((string)node.title?.english)
                    ? (string)node.title?.romaji
                    : (string)node.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                var isRelMovie = IsMovieFormat((string)node.format);
                var stremioType = isRelMovie ? MetaType.movie.ToString() : MetaType.series.ToString();
                var encodedId = Uri.EscapeDataString($"{anilistPrefix}{relId.Value}");
                result.Add(new Link
                {
                    name = name,
                    category = label,
                    url = $"https://web.stremio.com/#/detail/{stremioType}/{encodedId}",
                    anilistId = relId.Value,
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
        /// groupers. The browse routes (/discover/tag/{name},
        /// /discover/staff/{id}, /discover/studio/{id}) want each card to
        /// hand off to /anime/{id} with the
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
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                AnilistHealthMonitor.RecordFailure();
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500) AnilistHealthMonitor.RecordFailure();
                return null;
            }
            AnilistHealthMonitor.RecordSuccess();

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
                                media(season: $season, seasonYear: $seasonYear, type: ANIME, isAdult: false) {
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

        public async Task<List<Meta>> GetNewEpisodesTodayAsync(
            AnimeService translateTo = AnimeService.Anilist,
            int tzOffsetMinutes = 0,
            bool groupSeasons = false)
        {
            // Bucket "today" against the viewer's local calendar day rather
            // than UTC. tzOffsetMinutes follows JS Date.getTimezoneOffset()
            // convention: minutes WEST of UTC (UTC+3 = -180, UTC-5 = +300).
            // Negate it to get the TimeSpan offset DateTimeOffset wants.
            // A UTC+3 viewer at local 02:16 (their day 16) sees the window
            // [16 00:00 local, 17 00:00 local) which translates to UTC
            // [15 21:00, 16 21:00) — yesterday-UTC's late evening through
            // today-UTC's afternoon. Without this, a rolling-now-±18h
            // window over-included yesterday's afternoon airings (still
            // within ±18h of UTC-midnight) and tomorrow's morning airings.
            var tz = TimeSpan.FromMinutes(-tzOffsetMinutes);
            var localNow = DateTimeOffset.UtcNow.ToOffset(tz);
            var localToday = new DateTimeOffset(localNow.Date, tz);
            var startUnix = localToday.ToUnixTimeSeconds();
            var endUnix = localToday.AddDays(1).ToUnixTimeSeconds();

            // Cache key includes the local-day + timezone offset so
            // viewers in different timezones get their own cached
            // bucket and each rotates at its own midnight. The hour
            // suffix limits per-tz cache lifetime to roughly an hour
            // so an airing-schedule shift propagates quickly.
            var cacheKey = $"anilist:new-episodes-today:{localToday:yyyyMMdd}:tz{tzOffsetMinutes}:{localNow:HH}";

            if (_cache.TryGetValue<List<Meta>>(cacheKey, out var cached) && cached != null)
            {
                // Cache stores anilist:N ids (one entry per hour, shared
                // across every viewer). Translate per-call into the
                // requesting service's native id space so a MAL/Kitsu
                // primary's clicks resolve directly. CloneMetas first so
                // the cached list itself doesn't get mutated.
                return await TranslateMetaIdsAsync(CloneMetas(cached), translateTo, groupSeasons);
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
                                        isAdult
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

                    // Dashboard shelf cache is shared across every viewer, so
                    // we can't honor a per-user showAdultContent toggle here.
                    // Match the safer default — explicit 18+ entries never
                    // surface on the dashboard for anyone. Users who opt in
                    // can still find them via Discover / search where the
                    // toggle is per-user.
                    if ((bool?)media.isAdult == true) continue;

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
                        airingEpisode = (int?)sched.episode,
                        airingAt = (long?)sched.airingAt,
                    });
                }

                if (result.Count > 0)
                {
                    // Final ascending sort by airingAt — the GraphQL `sort: TIME`
                    // already returns ascending, but make the order an explicit
                    // post-condition so a future upstream change can't quietly
                    // reorder the dashboard shelf.
                    result = result.OrderBy(m => m.airingAt ?? long.MaxValue).ToList();

                    // Hold the snapshot for one hour. With the rolling
                    // window the dashboard never shows episodes more than
                    // LookbackHours old — caching longer would let the
                    // user-facing "today" drift past that threshold
                    // before the next refresh.
                    _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                    });
                }
                // Same translate-on-return contract as the cache-hit path so
                // the caller's primary determines the id-space regardless of
                // whether this call populated or read the cache.
                return await TranslateMetaIdsAsync(CloneMetas(result), translateTo, groupSeasons);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetNewEpisodesTodayAsync failed: {ex.Message}");
                return [];
            }
        }

        public async Task<List<UpcomingEpisode>> GetUpcomingEpisodesAsync(long startUnix, long endUnix)
        {
            // Same airingSchedules shape as GetNewEpisodesTodayAsync above, but
            // parameterised on an arbitrary window with no caching — the caller's
            // cron tick is the cadence (every 5 min). Pulls up to 100 entries per
            // window which comfortably covers a 24h horizon (~50-80 airings/day
            // peak Saturday); if the window grows we'd need to paginate.
            try
            {
                var requestBody = SerializeObject(new
                {
                    query = @"
                        query ($startUnix: Int, $endUnix: Int, $page: Int) {
                            Page(page: $page, perPage: 100) {
                                airingSchedules(airingAt_greater: $startUnix, airingAt_lesser: $endUnix, sort: TIME) {
                                    airingAt
                                    episode
                                    media {
                                        id
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

                var result = new List<UpcomingEpisode>();
                foreach (var sched in schedules)
                {
                    var media = sched.media;
                    if (media == null) continue;

                    var anilistId = (int?)media.id;
                    var episode = (int?)sched.episode;
                    var airingAt = (long?)sched.airingAt;
                    if (!anilistId.HasValue || !episode.HasValue || !airingAt.HasValue) continue;

                    var name = string.IsNullOrEmpty((string)media.title?.english)
                        ? (string)media.title?.romaji
                        : (string)media.title?.english;
                    if (string.IsNullOrEmpty(name)) continue;

                    result.Add(new UpcomingEpisode(
                        AnilistId: anilistId.Value,
                        Title: name,
                        Episode: episode.Value,
                        AiringAt: airingAt.Value,
                        CoverImage: (string)media.coverImage?.large));
                }
                return result;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetUpcomingEpisodesAsync failed: {ex.Message}");
                return [];
            }
        }

        // 1h TTL is the same horizon the notification scheduler treats as
        // fresh enough to act on — beyond that, the daily refresh + per-
        // minute tick combo will have re-pulled the global schedule anyway.
        // Caching per anime keeps a hot detail page from hammering AniList
        // on repeated reloads (e.g. an anime in active discussion on a forum
        // funnels traffic at one id) without ever drifting past the window
        // the notifier already considers actionable.
        private static readonly TimeSpan AiringScheduleCacheDuration = TimeSpan.FromHours(1);

        public async Task<Dictionary<int, long>> GetAiringScheduleByAnilistIdAsync(int anilistId)
        {
            if (anilistId <= 0) return [];

            var cacheKey = $"anilist:airingSchedule:{anilistId}";
            if (_cache.TryGetValue<Dictionary<int, long>>(cacheKey, out var cached) && cached != null)
                return cached;

            // Single Media.airingSchedule fetch with a generous perPage so a
            // standard 12/24-episode cour resolves in one round-trip. AniList
            // caps perPage at 50; the rare long-form continuous run (Naruto-
            // style) would need pagination, but those are vanishingly few in
            // the Cinemeta-mapped catalog we hit this for.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            airingSchedule(perPage: 50) {
                                nodes { episode airingAt }
                            }
                        }
                    }",
                variables = new { id = anilistId }
            });

            try
            {
                var data = await PostJsonAsync(requestBody);
                var nodes = data?.Media?.airingSchedule?.nodes;
                var result = new Dictionary<int, long>();
                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        var episode = (int?)node.episode;
                        var airingAt = (long?)node.airingAt;
                        if (!episode.HasValue || !airingAt.HasValue) continue;
                        // Earliest wins when AniList returns duplicate rows
                        // for the same episode (split-cour shows occasionally
                        // do this around airing-day shifts) so the click gate
                        // unlocks at the first real airing, not a re-air.
                        if (result.TryGetValue(episode.Value, out var existing) && existing <= airingAt.Value)
                            continue;
                        result[episode.Value] = airingAt.Value;
                    }
                }
                _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = AiringScheduleCacheDuration,
                });
                return result;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetAiringScheduleByAnilistIdAsync failed: {ex.Message}");
                return [];
            }
        }

        public async Task<List<Meta>> GetRelatedAsync(int anilistId, AnimeService translateTo = AnimeService.Anilist, bool groupSeasons = false)
            => await TranslateMetaIdsAsync(
                CloneMetas((await FetchSidedataAsync(anilistId)).Related),
                translateTo, groupSeasons);

        public async Task<List<Link>> GetRelatedLinksAsync(int anilistId)
            => (await FetchSidedataAsync(anilistId)).RelatedLinks;

        // Sidedata is cached in-memory; we Clone the metas before
        // translation so the per-call native-id translation doesn't mutate
        // the cached list (which would leak across viewers with different
        // primary services).
        private static List<Meta> CloneMetas(List<Meta> source)
        {
            if (source == null || source.Count == 0) return [];
            var copy = new List<Meta>(source.Count);
            foreach (var m in source)
            {
                copy.Add(new Meta
                {
                    id = m.id,
                    name = m.name,
                    poster = m.poster,
                    type = m.type,
                    score = m.score,
                    episodes = m.episodes,
                    year = m.year,
                    format = m.format,
                    airingEpisode = m.airingEpisode,
                    airingAt = m.airingAt,
                });
            }
            return copy;
        }

        public async Task<List<Meta>> TranslateMetaIdsAsync(List<Meta> metas, AnimeService translateTo, bool groupSeasons = false)
        {
            if (metas == null || metas.Count == 0) return metas;
            // groupSeasons=true short-circuits the AniList early-return below:
            // an AniList primary with grouping on still wants IMDb-prefixed ids
            // on the cards so click-throughs land on the franchise umbrella
            // page (per /configure's "Group anime seasons" toggle), not on the
            // per-cour anilist:N detail.
            if (translateTo == AnimeService.Anilist && !groupSeasons) return metas;
            await _mappingService.EnsureLoadedAsync();
            foreach (var meta in metas)
            {
                if (string.IsNullOrEmpty(meta?.id) || !meta.id.StartsWith(anilistPrefix)) continue;
                if (!int.TryParse(meta.id[anilistPrefix.Length..], out var anilistId)) continue;
                meta.id = groupSeasons
                    ? await ResolveExternalIdAsync(anilistId, translateTo)
                    : await ResolveNativeIdAsync(anilistId, translateTo);
            }
            return metas;
        }

        /// <summary>
        /// Per-user id rewrite for metas that already carry a service-native
        /// or imdb id (i.e. weren't produced by AnilistFallback and so didn't
        /// pass through <see cref="TranslateMetaIdsAsync"/>). Handles
        /// recommendations + the search dedup post-pass: per-service
        /// GetAnimeByIdAsync emits mal:/kitsu:/anilist: ids depending on the
        /// caller's primary, but a user with grouping on wants every card to
        /// surface as imdb:tt... so franchise click-throughs land on the
        /// canonical /anime/imdb:tt... page everywhere. groupSeasons=false is
        /// the no-op pass-through; ids the mapping table doesn't recognise
        /// stay unchanged either way so the card still links somewhere
        /// reasonable.
        /// </summary>
        public async Task<List<Meta>> ApplyGroupingToMetasAsync(List<Meta> metas, bool groupSeasons)
        {
            if (!groupSeasons || metas == null || metas.Count == 0) return metas;
            await _mappingService.EnsureLoadedAsync();
            foreach (var meta in metas)
            {
                if (string.IsNullOrEmpty(meta?.id)) continue;
                // Already imdb-prefixed → nothing to rewrite. tmdb is also
                // an acceptable grouped id (next-best after imdb in the
                // ResolveGroupedId chain) so we leave it alone too.
                if (meta.id.StartsWith(imdbPrefix) || meta.id.StartsWith(tmdbPrefix)) continue;

                var imdbId = await ResolveImdbAsync(meta.id);
                if (!string.IsNullOrEmpty(imdbId)) meta.id = imdbId;
            }
            return metas;
        }

        /// <summary>
        /// Resolves any service-prefixed id (anilist:/mal:/kitsu:) to the
        /// imdb tt-id from the cross-service mapping table, or null when no
        /// imdb mapping exists. Shared between the meta-translation path
        /// above and the notification-link rewrite the dispatcher uses to
        /// honor the per-user grouping pref when constructing the click
        /// target.
        /// </summary>
        private async Task<string> ResolveImdbAsync(string animeId)
        {
            if (string.IsNullOrEmpty(animeId)) return null;
            AnimeIdMapping mapping = null;
            try
            {
                if (animeId.StartsWith(anilistPrefix))
                    mapping = await _mappingService.GetAnilistMapping(animeId);
                else if (animeId.StartsWith(malPrefix))
                    mapping = await _mappingService.GetMalMapping(animeId);
                else if (animeId.StartsWith(kitsuPrefix))
                    mapping = await _mappingService.GetKitsuMapping(animeId);
            }
            catch { return null; }
            var imdb = mapping?.ImdbId;
            return !string.IsNullOrEmpty(imdb) && imdb.StartsWith("tt") ? imdb : null;
        }

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
        private async Task<List<Meta>> BuildBrowseMetasAsync(dynamic mediaArr, AnimeService translateTo, bool hideAdult = false, bool groupSeasons = false)
        {
            if (mediaArr == null) return [];
            await _mappingService.EnsureLoadedAsync();
            var seen = new HashSet<string>();
            var result = new List<Meta>();
            foreach (var media in mediaArr)
            {
                var anilistId = (int?)media.id;
                if (!anilistId.HasValue) continue;

                // 18+ gate — per-user setting threaded through from the
                // discover-side controllers. Each query selects isAdult so
                // we can filter here without a second round-trip; queries
                // that need a free-text-tag style filter (Page.media) also
                // pass isAdult:false at the GraphQL layer, but the
                // Staff.staffMedia / Studio.media connections don't expose
                // that arg so the client-side filter is what enforces it
                // for those paths.
                if (hideAdult && (bool?)media.isAdult == true) continue;

                // Per-user grouping pref picks the id-resolver: imdb-first
                // when on (matches Stremio's grouped catalog id space), the
                // primary's native id when off (so Manage Entry / detail
                // hand-offs land on the per-cour page). seen-set dedup
                // collapses cours that share an imdb id into a single card
                // when grouping is on.
                var externalId = groupSeasons
                    ? await ResolveExternalIdAsync(anilistId.Value, translateTo)
                    : await ResolveNativeIdAsync(anilistId.Value, translateTo);
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
            // Sort by the display name we just built (english title, falling
            // back to romaji) so the result order matches what the user
            // actually reads on the cards. Library does the same; consistency
            // between browse surfaces lets the user predict where a specific
            // anime will be in the grid.
            return result
                .OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<List<Meta>> GetByTagAsync(string tag, AnimeService translateTo, string skip = null, bool hideAdult = false, bool groupSeasons = false)
        {
            if (string.IsNullOrWhiteSpace(tag)) return [];
            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;
            // Page.media() supports isAdult — filter server-side so we
            // don't waste a page slot on entries we'd drop anyway.
            var adultArg = hideAdult ? ", isAdult: false" : string.Empty;

            var requestBody = SerializeObject(new
            {
                query = $@"
                    query ($page: Int, $perPage: Int, $tag: String) {{
                        Page(page: $page, perPage: $perPage) {{
                            media(type: ANIME, tag: $tag, sort: TITLE_ROMAJI{adultArg}) {{
                                id
                                isAdult
                                format
                                episodes
                                averageScore
                                seasonYear
                                title {{ english romaji }}
                                coverImage {{ large }}
                                description
                            }}
                        }}
                    }}",
                variables = new { page, perPage = PageSize, tag },
            });

            var data = await PostJsonAsync(requestBody);
            return await BuildBrowseMetasAsync(data?.Page?.media, translateTo, hideAdult, groupSeasons);
        }

        public async Task<(List<Meta> Items, bool HasNextPage)> GetByTagPageAsync(string tag, AnimeService translateTo, int page = 1, bool hideAdult = false, bool groupSeasons = false)
        {
            if (string.IsNullOrWhiteSpace(tag)) return ([], false);
            if (page < 1) page = 1;
            var adultArg = hideAdult ? ", isAdult: false" : string.Empty;

            var requestBody = SerializeObject(new
            {
                query = $@"
                    query ($page: Int, $perPage: Int, $tag: String) {{
                        Page(page: $page, perPage: $perPage) {{
                            pageInfo {{ hasNextPage }}
                            media(type: ANIME, tag: $tag, sort: TITLE_ROMAJI{adultArg}) {{
                                id
                                isAdult
                                format
                                episodes
                                averageScore
                                seasonYear
                                title {{ english romaji }}
                                coverImage {{ large }}
                                description
                            }}
                        }}
                    }}",
                variables = new { page, perPage = PageSize, tag },
            });

            var data = await PostJsonAsync(requestBody);
            var items = await BuildBrowseMetasAsync(data?.Page?.media, translateTo, hideAdult, groupSeasons);
            bool hasNext = data?.Page?.pageInfo?.hasNextPage != null
                && (bool)data.Page.pageInfo.hasNextPage;
            return (items, hasNext);
        }

        public async Task<List<TagSummary>> GetTagsListAsync()
        {
            const string cacheKey = "anilist:tags-list:by-category";
            if (_cache.TryGetValue<List<TagSummary>>(cacheKey, out var cached) && cached != null)
            {
                return cached;
            }

            // MediaTagCollection isn't paginated upstream — AniList returns
            // every tag in one shot (a few hundred entries). Adult-only
            // tags are dropped here rather than at render time so the
            // server-side cache stays consistent with what we ever surface.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query {
                        MediaTagCollection {
                            name
                            category
                            description
                            isAdult
                        }
                    }",
            });

            var data = await PostJsonAsync(requestBody);
            var tags = new List<TagSummary>();
            if (data?.MediaTagCollection != null)
            {
                foreach (var t in data.MediaTagCollection)
                {
                    if (t == null) continue;
                    var name = (string)t.name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (t.isAdult != null && (bool)t.isAdult) continue;
                    tags.Add(new TagSummary
                    {
                        Name = name,
                        Category = (string)t.category,
                        Description = (string)t.description,
                    });
                }
            }

            // Sort by category, then name — lets the view render section
            // headers (Theme – Action, Theme – Romance, Setting-Time, …)
            // without an extra group-by pass. Tags with no category land
            // under an "Other" bucket at the top alphabetically.
            tags.Sort((a, b) =>
            {
                var ca = a.Category ?? string.Empty;
                var cb = b.Category ?? string.Empty;
                var byCat = string.Compare(ca, cb, StringComparison.OrdinalIgnoreCase);
                return byCat != 0 ? byCat : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            if (tags.Count > 0)
            {
                _cache.Set(cacheKey, tags, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24),
                });
            }
            return tags;
        }

        public async Task<(string Name, List<Meta> Items)> GetStaffMediaAsync(int staffId, AnimeService translateTo, string skip = null, bool hideAdult = false, bool groupSeasons = false)
        {
            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;

            // Staff.staffMedia doesn't accept an isAdult filter arg on the
            // GraphQL connection, so we select the field and filter inside
            // BuildBrowseMetasAsync. Costs one boolean per node — cheap.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int, $page: Int, $perPage: Int) {
                        Staff(id: $id) {
                            name { full }
                            staffMedia(type: ANIME, sort: TITLE_ROMAJI, page: $page, perPage: $perPage) {
                                edges {
                                    node {
                                        id
                                        isAdult
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
            return (name, await BuildBrowseMetasAsync(nodes, translateTo, hideAdult, groupSeasons));
        }

        public async Task<(string Name, List<Meta> Items, bool HasNextPage)> GetStudioMediaAsync(int studioId, AnimeService translateTo, int page = 1, bool hideAdult = false, bool groupSeasons = false)
        {
            if (page < 1) page = 1;

            // Studio.media doesn't accept an isAdult filter arg on the
            // GraphQL connection — same as Staff.staffMedia. Select the
            // field and filter inside BuildBrowseMetasAsync.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int, $page: Int, $perPage: Int) {
                        Studio(id: $id) {
                            name
                            media(sort: TITLE_ROMAJI, page: $page, perPage: $perPage) {
                                pageInfo { hasNextPage }
                                edges {
                                    node {
                                        id
                                        isAdult
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
            if (studio == null) return (null, [], false);

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

            // hasNextPage from AniList itself — independent of how many
            // anime survived the manga filter above. A page can render
            // zero cards (all manga) while more anime pages still
            // follow, so the paginator can't infer end-of-list from
            // list size alone.
            bool hasNext = studio.media?.pageInfo?.hasNextPage != null
                && (bool)studio.media.pageInfo.hasNextPage;

            return (name, await BuildBrowseMetasAsync(nodes, translateTo, hideAdult, groupSeasons), hasNext);
        }

        public async Task<(List<StudioSummary> Studios, bool HasNextPage)> GetStudiosListAsync(int page = 1)
        {
            if (page < 1) page = 1;

            var cacheKey = $"anilist:studios-list:by-favourites:p{page}";
            if (_cache.TryGetValue<(List<StudioSummary>, bool)>(cacheKey, out var cached) && cached.Item1 != null)
            {
                return cached;
            }

            // perPage=50 matches the discover paginator's chunk size so the
            // user feels the same scroll-loaded cadence between anime and
            // studio listings. FAVOURITES_DESC surfaces the studios the
            // user is most likely to recognise first (Mappa, Madhouse,
            // Ghibli, …) and tapers down into long-tail entries as they
            // scroll. isAnimationStudio + the media pageInfo.total filter
            // out manga / LN labels and empty entries so every rendered
            // tile points at a catalog with at least one anime. NB:
            // Studio.media does NOT accept a `type` argument — passing
            // one 500s the whole query — so the anime/non-anime cut
            // happens via isAnimationStudio.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($page: Int, $perPage: Int) {
                        Page(page: $page, perPage: $perPage) {
                            pageInfo { hasNextPage }
                            studios(sort: FAVOURITES_DESC) {
                                id
                                name
                                isAnimationStudio
                                media { pageInfo { total } }
                            }
                        }
                    }",
                variables = new { page, perPage = 50 },
            });

            var data = await PostJsonAsync(requestBody);
            if (data?.Page?.studios == null)
            {
                // Upstream errored / rate-limited — don't cache, let the
                // next request retry. Empty list + HasNextPage=false
                // stops the client paginator gracefully without 500ing.
                return (new List<StudioSummary>(), false);
            }

            var studios = new List<StudioSummary>();
            foreach (var s in data.Page.studios)
            {
                if (s == null) continue;
                var name = (string)s.name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                bool isAnimation = s.isAnimationStudio != null && (bool)s.isAnimationStudio;
                if (!isAnimation) continue;
                int count = 0;
                if (s.media?.pageInfo?.total != null)
                {
                    count = (int)s.media.pageInfo.total;
                }
                if (count <= 0) continue;
                studios.Add(new StudioSummary
                {
                    Id = (int)s.id,
                    Name = name,
                    AnimeCount = count,
                });
            }

            // hasNextPage comes from AniList's own pageInfo — independent
            // of how many studios survived our client-side filter. A page
            // can render zero tiles (all 50 entries were manga labels)
            // and still have more real pages after; the caller must use
            // this flag, not list size, to decide when to stop scrolling.
            bool hasNext = data.Page.pageInfo?.hasNextPage != null && (bool)data.Page.pageInfo.hasNextPage;

            var result = (studios, hasNext);
            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24),
            });
            return result;
        }
    }
}
