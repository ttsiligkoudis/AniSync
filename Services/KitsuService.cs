using AnimeList.Models;
using AnimeList.Services.Interfaces;
using System.Net.Http.Headers;
using System.Text;

namespace AnimeList.Services
{
    public class KitsuService : IKitsuService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly string _kitsuApi = "https://kitsu.io/api/edge";

        public KitsuService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null)
        {
            var tmpStr = list.HasValue ? $"&filter[status]={GetListTypeString(list.Value, tokenData)}" : "";
            var animeFilter = !string.IsNullOrWhiteSpace(animeId) ? $"&filter[animeId]={animeId.Replace(kitsuPrefix, "")}" : "";

            var str = list == ListType.Trending_Desc
                ? $"{_kitsuApi}/trending/anime?filter[status]={GetListTypeString(ListType.Current, tokenData)}"
                : $"{_kitsuApi}/users/{tokenData.user_id}/library-entries?filter[kind]=anime&include=anime{tmpStr}{animeFilter}";

            str = $"{str}&page[limit]=20";

            if (!string.IsNullOrEmpty(skip))
            {
                str = $"{str}&page[offset]={skip}";
            }

            var request = new HttpRequestMessage(HttpMethod.Get, str);

            if (!string.IsNullOrWhiteSpace(tokenData?.access_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];

            var content = await response.Content.ReadAsStringAsync();
            var entries = DeserializeObject<dynamic>(content);

            // Build O(1) lookup for included anime by id
            var includedById = new Dictionary<string, dynamic>();
            if (list != ListType.Trending_Desc && entries.included != null)
            {
                foreach (var inc in entries.included)
                {
                    includedById[(string)inc.id] = inc;
                }
            }

            var animeList = new List<Meta>();
            foreach (var entry in entries.data)
            {
                dynamic included = list == ListType.Trending_Desc
                    ? entry
                    : includedById.GetValueOrDefault((string)entry.relationships.anime.data.id);

                if (included == null) continue;

                if (list == ListType.Current && (string)included.attributes.status is "tba" or "unreleased" or "upcoming") continue;

                int kitsuId = (int)included.id;
                string imdbId = await _mappingService.GetImdbIdByKitsuIdAsync(kitsuId);

                animeList.Add(new Meta
                {
                    id = imdbId ?? $"{kitsuPrefix}{included.id}",
                    type = ((string)included.attributes.subtype).Equals("movie", StringComparison.OrdinalIgnoreCase) ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = included.attributes.titles.en,
                    poster = included.attributes.posterImage != null ? (string)included.attributes.posterImage.large : null,
                    descriptionRich = included.attributes.description,
                    entryId = list == ListType.Trending_Desc ? null : entry.id,
                });
            }

            return animeList;
        }

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData)
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith(kitsuPrefix)) return null;

            var request = new HttpRequestMessage(HttpMethod.Get, $"{_kitsuApi}/anime/{id.Replace(kitsuPrefix, "")}?include=genres,episodes");
            if (!string.IsNullOrWhiteSpace(tokenData?.access_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var results = DeserializeObject<dynamic>(content);

            var entry = results.data;
            int kitsuId = (int)entry.id;
            string imdbId = await _mappingService.GetImdbIdByKitsuIdAsync(kitsuId);

            var anime = new Meta
            {
                id = id,
                type = (string)entry.attributes.subtype == "movie" ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = entry.attributes.titles.en,
                poster = entry.attributes.posterImage != null ? (string)entry.attributes.posterImage.large : null,
                descriptionRich = entry.attributes.description,
                //genres = entry.relationships.genres.data.ToObject<List<string>>(),
                background = entry.attributes.coverImage != null ? (string)entry.attributes.coverImage.large : null
            };

            if (!string.IsNullOrEmpty((string)entry.attributes.youtubeVideoId))
            {
                anime.trailers.Add(new Trailer(entry.attributes.youtubeVideoId));
                anime.trailerStreams.Add(new TrailerStream(entry.attributes.youtubeVideoId, anime.name));
            }

            foreach (var episode in results.included)
            {
                var video = new Video
                {
                    thumbnail = episode.attributes.thumbnail != null ? episode.attributes.thumbnail.large : null,
                    season = episode.attributes.seasonNumber,
                    episode = episode.attributes.number
                };

                video.id = $"{id}:{video.episode}";
                video.title = string.IsNullOrEmpty((string)episode.attributes.canonicalTitle)
                    ? $"Episode {video.episode}"
                    : episode.attributes.canonicalTitle;

                if (string.IsNullOrEmpty(video.title)) continue;

                anime.videos.Add(video);
            }

            return anime;
        }

        public async Task UpdateEpisodeProgressAsync(TokenData tokenData, string animeId, int episode)
        {
            if (string.IsNullOrEmpty(animeId)
                || string.IsNullOrWhiteSpace(tokenData?.user_id)
                || tokenData?.anonymousUser == true) return;

            var entryId = (await GetAnimeListAsync(tokenData, null, null, animeId)).FirstOrDefault()?.entryId;

            if (string.IsNullOrEmpty(entryId)) return;

            //// Fetch total episode count to determine if this completes the series
            int? totalEpisodes = await GetTotalEpisodesAsync(tokenData, animeId);
            bool isCompleted = totalEpisodes.HasValue && totalEpisodes.Value > 0 && episode >= totalEpisodes.Value;

            var obj = new
            {
                data = new
                {
                    type = "libraryEntries",
                    id = entryId,
                    attributes = new
                    {
                        progress = episode,
                        status = isCompleted ? "completed" : "current"
                    }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Patch, $"{_kitsuApi}/library-entries/{entryId}")
            {
                Content = new StringContent(SerializeObject(obj), Encoding.UTF8, "application/vnd.api+json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

            var client = _clientFactory.CreateClient();
            await client.SendAsync(request);
        }

        private async Task<int?> GetTotalEpisodesAsync(TokenData tokenData, string animeId)
        {
            var kitsuId = animeId.Replace(kitsuPrefix, "");

            var request = new HttpRequestMessage(HttpMethod.Get, $"{_kitsuApi}/anime/{kitsuId}?fields[anime]=episodeCount");
            if (!string.IsNullOrWhiteSpace(tokenData?.access_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var result = DeserializeObject<dynamic>(content);

            return (int?)result?.data?.attributes?.episodeCount;
        }
    }
}

