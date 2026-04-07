using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

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

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null)
        {
            // Kitsu does not support seasonal catalogs; return empty for seasonal list types
            if (list == ListType.Seasonal) return [];

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

                var mapping = await _mappingService.GetKitsuMapping((string)included.id);

                var externalId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                                 !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                                 $"{kitsuPrefix}{included.id}";

                animeList.Add(new Meta
                {
                    id = externalId,
                    //type = IsMovieFormat((string)included.attributes.subtype) ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    type = MetaType.anime.ToString(),
                    name = included.attributes.titles.en,
                    poster = included.attributes.posterImage != null ? (string)included.attributes.posterImage.large : null,
                    descriptionRich = included.attributes.description,
                    entryId = list == ListType.Trending_Desc ? null : entry.id,
                    entryStatus = entry.status,
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

            var mapping = await _mappingService.GetKitsuMapping((string)entry.id);

            var externalId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                             !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                             $"{kitsuPrefix}{entry.id}";

            var isMovie = IsMovieFormat((string)entry.attributes.subtype);

            var anime = new Meta
            {
                id = externalId,
                //type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                type = MetaType.anime.ToString(),
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

            if (!isMovie)
            {
                var episodeNumber = 1;

                foreach (var episode in results.included)
                {
                    object seasonNumber = 1;
                    object thumbnail = null;

                    var jObj = (JObject)episode.attributes;

                    var token = jObj["seasonNumber"];
                    if (token != null && token.Type != JTokenType.Null)
                    {
                        seasonNumber = token;
                    }

                    token = jObj["thumbnail"];
                    if (token != null && token.Type != JTokenType.Null)
                    {
                        thumbnail = token;
                    }

                    var video = new Video
                    {
                        id = $"{externalId}:{episode.attributes.seasonNumber}:{episode.attributes.number}",
                        thumbnail = thumbnail?.ToString(),
                        season = int.Parse(seasonNumber.ToString()),
                        episode = episodeNumber
                    };

                    video.id = $"{id}:{video.episode}";
                    video.title = string.IsNullOrEmpty((string)episode.attributes.canonicalTitle)
                        ? $"Episode {video.episode}"
                        : episode.attributes.canonicalTitle;

                    if (string.IsNullOrEmpty(video.title)) continue;

                    anime.videos.Add(video);
                    episodeNumber++;
                }
            }

            return anime;
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

        public async Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu, season);

            if (string.IsNullOrEmpty(resolvedKitsuId)) return null;

            var kitsuAnimeId = resolvedKitsuId.Replace(kitsuPrefix, "");

            // Fetch library entry for this user + anime
            var meta = (await GetAnimeListAsync(tokenData, null, null, $"{kitsuPrefix}{kitsuAnimeId}")).FirstOrDefault();

            // Fetch total episodes
            int? totalEpisodes = await GetTotalEpisodesAsync(tokenData, $"{kitsuPrefix}{kitsuAnimeId}");

            return new AnimeEntry
            {
                EntryId = meta?.entryId,
                MediaId = kitsuAnimeId,
                Status = meta?.entryStatus ?? "current",
                Progress = 0, // Kitsu library-entries include doesn't expose progress in GetAnimeListAsync; default to 0
                TotalEpisodes = totalEpisodes
            };
        }

        public async Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress, string status = null)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu, season);

            if (string.IsNullOrEmpty(resolvedKitsuId)) return;

            var kitsuAnimeId = resolvedKitsuId.Replace(kitsuPrefix, "");

            var meta = (await GetAnimeListAsync(tokenData, null, null, $"{kitsuPrefix}{kitsuAnimeId}")).FirstOrDefault();

            if (string.IsNullOrEmpty(status))
            {
                status = meta?.entryStatus;

                //int? totalEpisodes = await GetTotalEpisodesAsync(tokenData, resolvedKitsuId);
                //bool isCompleted = totalEpisodes.HasValue && totalEpisodes.Value > 0 && progress >= totalEpisodes.Value;
                //status = GetListTypeString(isCompleted ? ListType.Completed : ListType.Current, tokenData);
            }

            if (!string.IsNullOrEmpty(meta?.entryId))
            {
                // Update existing entry
                var obj = new
                {
                    data = new
                    {
                        type = "libraryEntries",
                        id = meta?.entryId,
                        attributes = new
                        {
                            progress,
                            status
                        }
                    }
                };

                var request = new HttpRequestMessage(HttpMethod.Patch, $"{_kitsuApi}/library-entries/{meta?.entryId}")
                {
                    Content = new StringContent(SerializeObject(obj), Encoding.UTF8, "application/vnd.api+json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

                var client = _clientFactory.CreateClient();
                await client.SendAsync(request);
            }
            else
            {
                // Create new entry
                var obj = new
                {
                    data = new
                    {
                        type = "libraryEntries",
                        attributes = new
                        {
                            progress,
                            status
                        },
                        relationships = new
                        {
                            user = new { data = new { type = "users", id = tokenData.user_id } },
                            anime = new { data = new { type = "anime", id = kitsuAnimeId } }
                        }
                    }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, $"{_kitsuApi}/library-entries")
                {
                    Content = new StringContent(SerializeObject(obj), Encoding.UTF8, "application/vnd.api+json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

                var client = _clientFactory.CreateClient();
                await client.SendAsync(request);
            }
        }
    }
}

