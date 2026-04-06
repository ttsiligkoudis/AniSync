using AnimeList.Models;
using AnimeList.Services.Interfaces;
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
            var meta = await GetTvShowAsync(tmdbId, seasons);
            return meta ?? await GetMovieAsync(tmdbId);
        }

        private async Task<Meta> GetTvShowAsync(string tmdbId, List<int> seasons)
        {
            var client = CreateAuthenticatedClient();
            var response = await client.GetAsync($"{_tmdbApi}/tv/{tmdbId}?append_to_response=videos,external_ids");

            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var result = DeserializeObject<dynamic>(content);

            var externalId = (string)result.external_ids?.imdb_id;
            externalId = string.IsNullOrEmpty(externalId) ? $"{tmdbPrefix}{tmdbId}" : externalId;

            var meta = new Meta
            {
                id = externalId,
                type = MetaType.series.ToString(),
                name = result.name,
                poster = BuildImageUrl((string)result.poster_path),
                background = BuildImageUrl((string)result.backdrop_path),
                descriptionRich = result.overview,
                genres = ((IEnumerable<dynamic>)result.genres)
                    .Select(g => (string)g.name)
                    .ToList(),
            };

            AddTrailers(meta, result.videos?.results);

            seasons ??= [];
            foreach (var seasonNumber in seasons)
            {
                meta.videos.AddRange(await GetSeasonEpisodesAsync(tmdbId, seasonNumber, externalId));
            }

            return meta;
        }

        private async Task<List<Video>> GetSeasonEpisodesAsync(string tmdbId, int seasonNumber, string externalId)
        {
            var client = CreateAuthenticatedClient();
            var response = await client.GetAsync($"{_tmdbApi}/tv/{tmdbId}/season/{seasonNumber}");

            if (!response.IsSuccessStatusCode) return [];

            var content = await response.Content.ReadAsStringAsync();
            var result = DeserializeObject<dynamic>(content);

            var videos = new List<Video>();
            if (result?.episodes == null) return videos;

            foreach (var episode in result.episodes)
            {
                videos.Add(new Video
                {
                    id = $"{externalId}:{seasonNumber}:{episode.episode_number}",
                    title = episode.name,
                    thumbnail = BuildImageUrl((string)episode.still_path),
                    season = seasonNumber,
                    episode = ((int)episode.episode_number),
                });
            }

            return videos;
        }

        private async Task<Meta> GetMovieAsync(string tmdbId)
        {
            var client = CreateAuthenticatedClient();
            var response = await client.GetAsync($"{_tmdbApi}/movie/{tmdbId}?append_to_response=videos");

            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var result = DeserializeObject<dynamic>(content);

            var meta = new Meta
            {
                id = $"{tmdbPrefix}{tmdbId}",
                type = MetaType.movie.ToString(),
                name = result.title,
                poster = BuildImageUrl((string)result.poster_path),
                background = BuildImageUrl((string)result.backdrop_path),
                descriptionRich = result.overview,
                genres = ((IEnumerable<dynamic>)result.genres)
                    .Select(g => (string)g.name)
                    .ToList(),
            };

            AddTrailers(meta, result.videos?.results);

            return meta;
        }

        private HttpClient CreateAuthenticatedClient()
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _tmdbReadToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private string BuildImageUrl(string path)
        {
            return string.IsNullOrEmpty(path) ? null : $"{_tmdbImageBase}{path}";
        }

        private static void AddTrailers(Meta meta, dynamic videoResults)
        {
            if (videoResults == null) return;

            foreach (var video in videoResults)
            {
                if ((string)video.site == "YouTube" && (string)video.type == "Trailer")
                {
                    meta.trailers.Add(new Trailer((string)video.key));
                    meta.trailerStreams.Add(new TrailerStream((string)video.key, meta.name));
                }
            }
        }
    }
}
