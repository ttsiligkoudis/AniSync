using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;

namespace AnimeList.Services
{
    public class CinemetaService : ICinemetaService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly ITokenService _tokenService;
        private readonly IAnimeMappingService _animeMapping;
        private readonly string _cinemetaApi = "https://v3-cinemeta.strem.io/meta"; //"https://cinemeta-live.strem.io/meta";

        public CinemetaService(IHttpClientFactory clientFactory, ITokenService tokenService, IAnimeMappingService animeMapping)
        {
            _clientFactory = clientFactory;
            _tokenService = tokenService;
            _animeMapping = animeMapping;
        }

        public async Task<string> GetAnimeByIdAsync(string config, string id, HttpRequest request = null)
        {
            try
            {
                var mapping = await _animeMapping.GetImdbMapping(id);

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

        public async Task<List<Video>> GetEpisodesAsync(string imdbId, int? cinemetaSeason, string targetExternalId)
        {
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);

                var response = await client.GetAsync($"{_cinemetaApi}/series/{imdbId}.json");
                if (!response.IsSuccessStatusCode) return [];

                var content = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(content);
                if (SafeGet(result, "meta", "videos") is not JArray videosArr) return [];

                // Filter to the target cour's season if provided. Renumber locally so each
                // cour renders as a clean S1 E1..N — Cinemeta's flat episodes don't align
                // with any service's per-cour numbering, and a single-cour view in Stremio
                // shouldn't show "Season 2 Episode 1" when the user clicked into a card
                // representing only that cour.
                return videosArr.OfType<JObject>()
                    .Where(v => !cinemetaSeason.HasValue || (int?)v["season"] == cinemetaSeason.Value)
                    .OrderBy(v => (int?)v["season"] ?? 0)
                    .ThenBy(v => (int?)v["episode"] ?? (int?)v["number"] ?? 0)
                    .Select((v, idx) => new Video
                    {
                        id = $"{targetExternalId}:1:{idx + 1}",
                        title = (string)v["name"] ?? (string)v["title"],
                        thumbnail = (string)v["thumbnail"],
                        season = 1,
                        episode = idx + 1,
                        released = (string)v["released"] ?? (string)v["firstAired"],
                        overview = (string)v["overview"] ?? (string)v["description"],
                    })
                    .ToList();
            }
            catch
            {
                return [];
            }
        }
    }
}
