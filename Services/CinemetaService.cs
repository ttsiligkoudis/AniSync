using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeList.Services
{
    public class CinemetaService : ICinemetaService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly ITokenService _tokenService;
        private readonly IAnimeMappingService _animeMapping;
        private readonly ILogger<CinemetaService> _logger;
        private readonly string _cinemetaApi = "https://v3-cinemeta.strem.io/meta"; //"https://cinemeta-live.strem.io/meta";

        public CinemetaService(IHttpClientFactory clientFactory, ITokenService tokenService,
            IAnimeMappingService animeMapping, ILogger<CinemetaService> logger)
        {
            _clientFactory = clientFactory;
            _tokenService = tokenService;
            _animeMapping = animeMapping;
            _logger = logger;
        }

        public async Task<string> GetAnimeByIdAsync(string config, string id, HttpRequest request = null)
        {
            try
            {
                var mapping = await _animeMapping.GetImdbMapping(id);

                if (mapping?.Any() != true) 
                {
                    await Task.Delay(3000);
                    return null;
                }

                var cinemetaType = !mapping.Any() || mapping.Any(w => w.Season.HasValue) ? "series" : "movie";
                var tokenData = await _tokenService.GetAccessTokenAsync(config);

                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);

                var response = await client.GetAsync($"{_cinemetaApi}/{cinemetaType}/{id}.json");
                if (!response.IsSuccessStatusCode) return null;

                var content = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(content);

                if (result["meta"] == null)
                {
                    //Search with other type in case of null
                    cinemetaType = cinemetaType == "series" ? "movie" : "series";
                    response = await client.GetAsync($"{_cinemetaApi}/{cinemetaType}/{id}.json");
                    if (!response.IsSuccessStatusCode) return null;

                    content = await response.Content.ReadAsStringAsync();
                    result = JObject.Parse(content);
                }

                if (result["meta"] == null) return null;

                if (!string.IsNullOrWhiteSpace(tokenData?.access_token) && !tokenData.anonymousUser && request != null)
                {
                    var manageUrl = $"{request.Scheme}://{request.Host}/{config}/Meta/ManageEntry/{id}";

                    var linksArray = result["meta"]?["links"] as JArray ?? [];
                    linksArray.Add(JObject.FromObject(new { name = "Manage Entry", category = "Manage", url = manageUrl }));
                    result["meta"]["links"] = linksArray;
                }

                return result.ToString();
            }
            catch
            {
                return null;
            }
        }

        public async Task<Meta> GetMetaAsync(string imdbId)
        {
            if (string.IsNullOrEmpty(imdbId)) return null;
            var content = await GetAnimeByIdAsync(null, imdbId);
            if (string.IsNullOrEmpty(content)) return null;

            try
            {
                // DateParseHandling.None keeps the `released` ISO strings on
                // Video as-is — Newtonsoft would otherwise coerce them to
                // local-time DateTime and the downstream airing-overlay /
                // future-check logic expects the raw ISO prefix.
                var result = JObject.Load(new JsonTextReader(new StringReader(content))
                {
                    DateParseHandling = DateParseHandling.None,
                });
                var m = result["meta"] as JObject;
                if (m == null) return null;

                var description = (string)m["description"] ?? string.Empty;
                var meta = new Meta(description)
                {
                    id = (string)m["id"] ?? imdbId,
                    name = (string)m["name"],
                    poster = (string)m["poster"],
                    background = (string)m["background"],
                    type = (string)m["type"] ?? MetaType.series.ToString(),
                };

                // Cinemeta hands imdbRating back as a string ("8.4"). 0/empty
                // means "no rating" — surface null in that case so the card's
                // score badge renders cleanly instead of "★ 0.0".
                var ratingRaw = (string)m["imdbRating"];
                if (!string.IsNullOrEmpty(ratingRaw)
                    && double.TryParse(ratingRaw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var rating)
                    && rating > 0)
                {
                    meta.score = Math.Round(rating, 1);
                }

                // releaseInfo shapes: "2020", "2020-", "2020-2024". The four-
                // digit prefix is always the start year (Cinemeta's own
                // convention) so a leading int parse gives us the right thing
                // without splitting on the dash.
                var releaseInfo = (string)m["releaseInfo"];
                if (!string.IsNullOrEmpty(releaseInfo) && releaseInfo.Length >= 4
                    && int.TryParse(releaseInfo[..4], out var year))
                {
                    meta.year = year;
                }

                // Runtime is "24 min" / "24" / null — parse the leading int so
                // the info row's "· 24 min" suffix renders when known.
                var runtime = (string)m["runtime"];
                if (!string.IsNullOrEmpty(runtime))
                {
                    var digits = new string(runtime.TakeWhile(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out var mins) && mins > 0) meta.avgDuration = mins;
                }

                if (m["genres"] is JArray genres)
                {
                    meta.genres = genres.Select(t => (string)t)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                }

                if (m["videos"] is JArray)
                {
                    meta.videos = SafeGet<List<Video>>(result, "meta", "videos") ?? [];
                }

                return meta;
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<Video>> GetEpisodesAsync(string imdbId, int? cinemetaSeason)
        {
            try
            {
                var content = await GetAnimeByIdAsync(null, imdbId);

                if (string.IsNullOrEmpty(content)) return [];

                var result = JObject.Load(new JsonTextReader(new StringReader(content))
                {
                    DateParseHandling = DateParseHandling.None,
                });

                var videos = SafeGet<List<Video>>(result, "meta", "videos");

                if (videos?.Any() != true) return [];

                return videos.Where(w => !cinemetaSeason.HasValue || w.season == cinemetaSeason).ToList();
            }
            catch
            {
                return [];
            }
        }

        public async Task<List<Video>> GetCourEpisodesAsync(
            string imdbId,
            int? cinemetaSeason,
            AnimeService service,
            int currentId,
            int currentEpisodeCount,
            Func<string, Task<(string? name, int? episodeCount)>> getSummary)
        {
            var allVideos = await GetEpisodesAsync(imdbId, null);
            if (allVideos.Count == 0)
            {
                _logger.LogInformation("GetCourEpisodes: imdb={Imdb} cinemeta returned 0 videos.", imdbId);
                return [];
            }

            allVideos = allVideos
                .Where(w => w.season > 0)
                .OrderBy(v => v.season)
                .ThenBy(v => v.episode)
                .ToList();

            // Per-service id field selector + mapping prefix used for the summary
            // lookup. Keeps the rest of the algorithm provider-agnostic.
            int? IdFor(AnimeIdMapping m) => service switch
            {
                AnimeService.Anilist => m.AnilistId,
                AnimeService.Kitsu => m.KitsuId,
                AnimeService.MyAnimeList => m.MalId,
                _ => null,
            };
            var prefix = GetServicePrefix(service);

            var mappings = (await _animeMapping.GetImdbMapping(imdbId))
                .OrderBy(m => m.Season ?? int.MaxValue)
                .ThenBy(IdFor)
                .ToList();

            // Single AniList entry for this IMDb id means the entire show is
            // represented by one AniList entry — never slice. Returning the
            // season-filtered subset here (mapping.Season is typically 1) would
            // truncate long-running shows like Naruto (anilist:20, 220 eps
            // across 5 IMDb seasons) to just the first IMDb season's 26
            // episodes. Must come BEFORE the season-filter branch below.
            if (mappings.Count <= 1)
            {
                _logger.LogInformation("GetCourEpisodes: imdb={Imdb} single-cour franchise — returning all {Count} videos.",
                    imdbId, allVideos.Count);
                return allVideos;
            }

            // Skip the season filter when more than one cour shares the same Season
            // number — the value is then per-source ambiguous and would lump multiple
            // cours together.
            var cinemetaSeasonIsWrong = mappings.Count(w => w.Season == cinemetaSeason) > 1;

            if (!cinemetaSeasonIsWrong && cinemetaSeason.HasValue)
            {
                var bySeason = allVideos.Where(v => v.season == cinemetaSeason.Value).ToList();
                if (bySeason.Count > 0)
                {
                    // Cap to AniList's claimed episode count when known.
                    // Cinemeta's season buckets occasionally carry
                    // trailing entries past the cour's real run — IMDb
                    // pre-populates placeholders for announced-but-
                    // unaired episodes, and a handful of franchises
                    // have extra special / movie entries filed under
                    // the same season number — which would inflate the
                    // detail page's episode list past what AniList
                    // reports. The cumulative-index branch below
                    // already takes only currentEpisodeCount entries;
                    // this matches that behaviour here.
                    if (currentEpisodeCount > 0 && bySeason.Count > currentEpisodeCount)
                    {
                        _logger.LogInformation("GetCourEpisodes: imdb={Imdb} season-filter matched {Count} videos for season={Season}, capping to {Cap} per AniList episode count.",
                            imdbId, bySeason.Count, cinemetaSeason, currentEpisodeCount);
                        bySeason = bySeason.Take(currentEpisodeCount).ToList();
                    }
                    else
                    {
                        _logger.LogInformation("GetCourEpisodes: imdb={Imdb} season-filter matched {Count} videos for season={Season}.",
                            imdbId, bySeason.Count, cinemetaSeason);
                    }
                    return bySeason;
                }
            }

            var index = mappings.FindIndex(m => IdFor(m) == currentId);
            if (index < 0)
            {
                _logger.LogInformation("GetCourEpisodes: imdb={Imdb} service={Service} id={Id} not found in mappings " +
                    "({Count} cours: [{Ids}]) — returning all {Total} videos.",
                    imdbId, service, currentId, mappings.Count,
                    string.Join(",", mappings.Select(m => IdFor(m))), allVideos.Count);
                return allVideos;
            }

            int cumulative = 0;
            var updateMappings = false;
            for (int i = 0; i < index; i++)
            {
                if (mappings[i].Season is null or > 0)
                {
                    var idHere = IdFor(mappings[i]);
                    if (!mappings[i].Episodes.HasValue && idHere.HasValue)
                    {
                        (mappings[i].Name, mappings[i].Episodes) = await getSummary($"{prefix}{idHere}");
                        updateMappings = true;
                    }
                    cumulative += mappings[i].Episodes ?? 0;
                }
            }

            if (updateMappings)
            {
                await _animeMapping.EnrichImdbMappings(mappings);
            }

            var take = currentEpisodeCount > 0 ? currentEpisodeCount : allVideos.Count - cumulative;
            var slice = allVideos.Skip(cumulative).Take(take).ToList();

            _logger.LogInformation("GetCourEpisodes: imdb={Imdb} service={Service} id={Id} index={Index}/{Count} " +
                "cumulative={Cumulative} take={Take} → {Slice} videos (cinemeta total={Total}, cinemetaSeasonIsWrong={Wrong}).",
                imdbId, service, currentId, index, mappings.Count, cumulative, take, slice.Count, allVideos.Count, cinemetaSeasonIsWrong);
            return slice;
        }
    }
}
