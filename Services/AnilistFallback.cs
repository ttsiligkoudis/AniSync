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

        public async Task<List<Meta>> GetAiringScheduleAsync(AnimeService translateTo, string skip = null)
        {
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

        public async Task<List<Meta>> GetRecommendationMetasAsync(int anilistId)
        {
            // Same query shape as the authenticated AnilistService meta-detail
            // recommendations subselection — coverImage + format + episodes +
            // averageScore + seasonYear so the carousel cards have the full
            // chrome (poster + score badge + format/eps/year info row). ids
            // stay in anilist:N space; AnimeController.Detail's mapping path
            // resolves them to the user's primary on click.
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
                        }
                    }",
                variables = new { id = anilistId }
            });

            var data = await PostJsonAsync(requestBody);
            var edges = data?.Media?.recommendations?.edges;
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
            //
            // The :v2 suffix bumps the key to invalidate the previous
            // version's bug-tainted 5000-cap entries — when the query was
            // declared with a `$year` variable but referenced as
            // `seasonYear: $year`, the binding silently failed on AniList's
            // side and every cell maxed out at the 5000 hard-cap. The v2
            // query renames to `$seasonYear` to match the existing seasonal
            // catalog query in AnilistService, which is known-correct.
            var (season, year) = GetSeasonAndYear(SeasonCurrent);
            var cacheKey = $"anilist:season-stats:v2:{season}:{year}";

            if (_cache.TryGetValue<(int, int, int)>(cacheKey, out var cached))
            {
                return cached;
            }

            // Single aliased GraphQL call — three Page queries each request
            // perPage:1 + pageInfo.total so we get the count without paying
            // for a result set. AniList's Page.pageInfo.total returns the
            // matching count regardless of perPage.
            //
            // Definitions:
            //   currentlyAiring: status RELEASING this season + year.
            //   totalThisSeason: every anime indexed for this season + year
            //     (including continuing ongoing series).
            //   newThisSeason: subset of totalThisSeason that's RELEASING or
            //     NOT_YET_RELEASED — proxy for "premiering this season"
            //     since AniList doesn't have a direct "is sequel" flag.
            //     Excludes shows that already FINISHED earlier this
            //     calendar season but were tagged with the season anyway
            //     (rare but exists for short re-airs).
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($season: MediaSeason, $seasonYear: Int) {
                        airing: Page(perPage: 1) {
                            pageInfo { total }
                            media(season: $season, seasonYear: $seasonYear, status: RELEASING, type: ANIME) { id }
                        }
                        newThis: Page(perPage: 1) {
                            pageInfo { total }
                            media(season: $season, seasonYear: $seasonYear, status_in: [RELEASING, NOT_YET_RELEASED], type: ANIME) { id }
                        }
                        total: Page(perPage: 1) {
                            pageInfo { total }
                            media(season: $season, seasonYear: $seasonYear, type: ANIME) { id }
                        }
                    }",
                variables = new { season, seasonYear = year }
            });

            try
            {
                var data = await PostJsonAsync(requestBody);
                if (data == null) return (0, 0, 0);
                var airing = (int?)data.airing?.pageInfo?.total ?? 0;
                var newThis = (int?)data.newThis?.pageInfo?.total ?? 0;
                var total = (int?)data.total?.pageInfo?.total ?? 0;

                // Sanity guard: AniList caps Page.pageInfo.total at 5000 when
                // a query matches >5000 entries (typically because filters
                // failed to bind). Current-season anime never exceed ~150;
                // a 5000 here means the upstream returned the unfiltered
                // catalog. Don't cache that — return zeros so the view
                // hides the strip and the next request retries fresh.
                if (total >= 5000) return (0, 0, 0);

                // Only cache non-zero successful results so a transient blip
                // (sandbox blocks GraphQL / rate-limit / 5xx) doesn't lock
                // in zeros for 24 hours.
                if (total > 0)
                {
                    _cache.Set(cacheKey, (airing, newThis, total), new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = SeasonStatsCacheDuration,
                    });
                }
                return (airing, newThis, total);
            }
            catch
            {
                return (0, 0, 0);
            }
        }
    }
}
