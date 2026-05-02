using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Stateless anonymous AniList client used by cross-service fallbacks.
    /// </summary>
    public class AnilistFallback : IAnilistFallback
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private const string _api = "https://graphql.anilist.co";
        private const int PageSize = 50;

        public AnilistFallback(IHttpClientFactory clientFactory, IAnimeMappingService mappingService)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
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

        /// <summary>
        /// Picks the most stable id for the requested service, in priority order:
        /// IMDb &gt; TMDB &gt; service-native (kitsu/anilist) &gt; anilist fallback.
        /// </summary>
        private async Task<string> ResolveExternalIdAsync(int anilistId, AnimeService translateTo)
        {
            var mapping = await _mappingService.GetAnilistMapping($"{anilistPrefix}{anilistId}");

            if (!string.IsNullOrEmpty(mapping?.ImdbId)) return mapping.ImdbId;
            if (!string.IsNullOrEmpty(mapping?.TmdbId)) return $"{tmdbPrefix}{mapping.TmdbId}";

            if (translateTo == AnimeService.Kitsu && mapping?.KitsuId != null)
                return $"{kitsuPrefix}{mapping.KitsuId}";

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
    }
}
