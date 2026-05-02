using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;

namespace AnimeList.Services
{
    public class TmdbService : ITmdbService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly string _tmdbApi = "https://api.themoviedb.org/3";
        private readonly string _tmdbImageBase = "https://image.tmdb.org/t/p/original";
        private readonly string _tmdbReadToken;

        public TmdbService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService, IConfiguration configuration)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _tmdbReadToken = configuration["TmdbReadToken"];
        }

        /// <summary>
        /// Fetches anime metadata from TMDB. Tries the TV endpoint first; falls back to movie.
        /// </summary>
        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData)
        {
            var tmdbId = id.Replace(tmdbPrefix, "");

            if (string.IsNullOrEmpty(tmdbId) || string.IsNullOrEmpty(_tmdbReadToken))
                return null;

            var mapping = await _mappingService.GetTmdbMapping(id);
            var seasons = mapping?.Where(w => w.Season.HasValue).Select(s => s.Season ?? 1).Distinct().ToList() ?? [1];

            // Most anime are TV series; try that first
            return await GetTvShowAsync(tmdbId, seasons) ?? await GetMovieAsync(tmdbId);
        }

        private async Task<Meta> GetTvShowAsync(string tmdbId, List<int> seasons)
        {
            var result = await GetJsonAsync($"{_tmdbApi}/tv/{tmdbId}?append_to_response=videos,external_ids");
            if (result == null) return null;

            var imdbId = SafeGet<string>(result, "external_ids", "imdb_id");
            var externalId = !string.IsNullOrEmpty(imdbId) ? imdbId : $"{tmdbPrefix}{tmdbId}";

            var meta = new Meta(SafeGet<string>(result, "overview"))
            {
                id = externalId,
                type = MetaType.series.ToString(),
                name = SafeGet<string>(result, "name"),
                poster = BuildImageUrl(SafeGet<string>(result, "poster_path")),
                background = BuildImageUrl(SafeGet<string>(result, "backdrop_path")),
                genres = ExtractGenres(result),
            };

            AddTrailers(meta, SafeGet(result, "videos", "results"));

            foreach (var seasonNumber in seasons ?? [])
                meta.videos.AddRange(await GetSeasonEpisodesAsync(tmdbId, seasonNumber, externalId));

            return meta;
        }

        private async Task<List<Video>> GetSeasonEpisodesAsync(string tmdbId, int seasonNumber, string externalId)
        {
            var result = await GetJsonAsync($"{_tmdbApi}/tv/{tmdbId}/season/{seasonNumber}");
            if (SafeGet(result, "episodes") is not JArray episodes) return [];

            return episodes.OfType<JObject>().Select(episode => new Video
            {
                id = $"{externalId}:{seasonNumber}:{(int?)episode["episode_number"] ?? 0}",
                title = (string)episode["name"],
                thumbnail = BuildImageUrl((string)episode["still_path"]),
                season = seasonNumber,
                episode = (int?)episode["episode_number"] ?? 0,
            }).ToList();
        }

        private async Task<Meta> GetMovieAsync(string tmdbId)
        {
            var result = await GetJsonAsync($"{_tmdbApi}/movie/{tmdbId}?append_to_response=videos");
            if (result == null) return null;

            var meta = new Meta(SafeGet<string>(result, "overview"))
            {
                id = $"{tmdbPrefix}{tmdbId}",
                type = MetaType.movie.ToString(),
                name = SafeGet<string>(result, "title"),
                poster = BuildImageUrl(SafeGet<string>(result, "poster_path")),
                background = BuildImageUrl(SafeGet<string>(result, "backdrop_path")),
                genres = ExtractGenres(result),
            };

            AddTrailers(meta, SafeGet(result, "videos", "results"));

            return meta;
        }

        private async Task<JObject> GetJsonAsync(string url)
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tmdbReadToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            return JObject.Parse(await response.Content.ReadAsStringAsync());
        }

        private string BuildImageUrl(string path) => string.IsNullOrEmpty(path) ? null : $"{_tmdbImageBase}{path}";

        private static List<string> ExtractGenres(JToken result)
        {
            if (SafeGet(result, "genres") is not JArray arr) return [];

            var names = new List<string>();
            foreach (var g in arr.OfType<JObject>())
            {
                var name = (string)g["name"];
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            return names;
        }

        private static void AddTrailers(Meta meta, JToken videoResults)
        {
            if (videoResults is not JArray videos) return;

            foreach (var video in videos.OfType<JObject>())
            {
                if ((string)video["site"] == "YouTube" && (string)video["type"] == "Trailer")
                {
                    var key = (string)video["key"];
                    if (string.IsNullOrEmpty(key)) continue;
                    meta.trailers.Add(new Trailer(key));
                    meta.trailerStreams.Add(new TrailerStream(key, meta.name));
                }
            }
        }
    }
}
