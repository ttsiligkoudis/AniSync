using AnimeList.Models;
using AnimeList.Services.Interfaces;
using System.Net.Http.Headers;

namespace AnimeList.Services
{
    public class AnilistService : IAnilistService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly string _anilistApi = "https://graphql.anilist.co";
        private readonly List<ListType> _userLists = [ListType.Current, ListType.Completed ];

        public AnilistService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null)
        {
            string requestBody = string.Empty;
            var resolvedAnimeId = animeId?.Replace(anilistPrefix, "");
            if (!list.HasValue || _userLists.Contains(list.Value))
            {
                if (!string.IsNullOrEmpty(resolvedAnimeId))
                {
                    var statusArg = list.HasValue ? $", status: {GetListTypeString(list.Value, tokenData)}" : string.Empty;

                    requestBody = SerializeObject(new
                    {
                        query = $@"
                        query ($userId: Int, $mediaId: Int) {{
                            MediaList(userId: $userId, mediaId: $mediaId, type: ANIME{statusArg}) {{
                                media {{
                                    id
                                    idMal
                                    format
                                    status
                                    title {{
                                        english
                                    }}
                                    coverImage {{
                                        large
                                    }}
                                    description
                                }}
                            }}
                        }}",
                        variables = new { userId = tokenData?.user_id, mediaId = resolvedAnimeId }
                    });
                }
                else
                {
                    var tmpStr = "userId: $userId, type: ANIME";

                    if (list.HasValue)
                    {
                        tmpStr = $"{tmpStr}, status: {GetListTypeString(list.Value, tokenData)}";
                    }

                    requestBody = SerializeObject(new
                    {
                        query = $@"
                        query ($userId: Int) {{
                            MediaListCollection({tmpStr}) {{
                                lists {{
                                    entries {{
                                        media {{
                                            id
                                            idMal
                                            format
                                            status
                                            title {{
                                                english
                                            }}
                                            coverImage {{
                                                large
                                            }}
                                            description
                                        }}
                                    }}
                                }}
                            }}
                        }}",
                        variables = new { userId = tokenData?.user_id }
                    });
                }
            }
            else
            {
                var mediaIdArg = !string.IsNullOrEmpty(resolvedAnimeId) ? ", id: $mediaId" : string.Empty;
                var query = """
                    query ($sort: [MediaSort], $mediaId: Int) {
                        Page {
                            media(sort: $sort, type: ANIME__MEDIA_ID_ARG__) {
                                id
                                idMal
                                format
                                title {
                                    english
                                }
                                coverImage {
                                    large
                                }
                                description
                            }
                        }
                    }
                    """.Replace("__MEDIA_ID_ARG__", mediaIdArg);

                requestBody = SerializeObject(new
                { 
                    query,
                    variables = new { sort = new List<string> { GetListTypeString(list.Value, tokenData) }, mediaId = resolvedAnimeId }
                });
            }

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData?.access_token);

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode) return [];

            var content = await response.Content.ReadAsStringAsync();
            IEnumerable<dynamic> result;
            var data = DeserializeObject<dynamic>(content).data;

            if (list == ListType.Trending_Desc)
            {
                result = data.Page.media;
            }
            else if (!string.IsNullOrEmpty(resolvedAnimeId))
            {
                result = data.MediaList == null ? Array.Empty<dynamic>() : [data.MediaList];
            }
            else
            {
                result = data.MediaListCollection.lists[0].entries;
            }

            var animeList = new List<Meta>();
            foreach (var entry in result)
            {
                var tmpEntry = entry;
                if (list != ListType.Trending_Desc)
                {
                    tmpEntry = entry.media;
                }

                if (list == ListType.Current && (string)tmpEntry.status == "NOT_YET_RELEASED") continue;

                int? malId = (int?)tmpEntry.idMal;
                int anilistId = (int)tmpEntry.id;
                string imdbId = malId.HasValue
                    ? await _mappingService.GetImdbIdByMalIdAsync(malId.Value)
                    : null;

                // Fall back to Kitsu ID when IMDb is unavailable (Torrentio supports Kitsu IDs)
                string externalId = imdbId;
                if (string.IsNullOrEmpty(externalId))
                {
                    int? kitsuId = await _mappingService.GetKitsuIdByAnilistIdAsync(anilistId);
                    externalId = kitsuId.HasValue ? $"{kitsuPrefix}{kitsuId}" : $"{anilistPrefix}{anilistId}";
                }

                animeList.Add(new Meta
                {
                    id = externalId,
                    malId = malId,
                    imdbId = imdbId,
                    type = (string)tmpEntry.format == "MOVIE" ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = tmpEntry.title.english,
                    poster = tmpEntry.coverImage.large,
                    descriptionRich = tmpEntry.description,
                });
            }

            return animeList;
        }

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData)
        {
            if (string.IsNullOrEmpty(id)) return null;

            int? kitsuId = null;

            // Convert Kitsu ID to AniList ID if needed
            if (id.StartsWith(kitsuPrefix))
            {
                var kitsuIdStr = id.Replace(kitsuPrefix, "");
                if (!int.TryParse(kitsuIdStr, out var kitsuIdVal)) return null;
                kitsuId = kitsuIdVal;
                var anilistIdVal = await _mappingService.GetAnilistIdByKitsuIdAsync(kitsuIdVal);
                if (!anilistIdVal.HasValue) return null;
                id = $"{anilistPrefix}{anilistIdVal.Value}";
            }

            if (!id.StartsWith(anilistPrefix)) return null;

            id = id.Replace(anilistPrefix, "");

            var query = @"
                query ($id: Int) {
                    Media(id: $id) {
                        id
                        idMal
                        format
                        title {
                            english
                        }
                        coverImage {
                            extraLarge
                        }
                        description,
                        genres,
                        trailer {
                            id,
                            site
                        },
                        streamingEpisodes {
                            title,
                            thumbnail
                        }
                    }
                }
            ";

            var variables = new { id };

            var ser = SerializeObject(new { query, variables });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(ser, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var result = DeserializeObject<dynamic>(content).data.Media;

            int? malId = (int?)result.idMal;
            int anilistId = (int)result.id;
            string imdbId = malId.HasValue
                ? await _mappingService.GetImdbIdByMalIdAsync(malId.Value)
                : null;

            // Fall back to Kitsu ID when IMDb is unavailable (Torrentio supports Kitsu IDs)
            string externalId = imdbId;
            if (string.IsNullOrEmpty(externalId))
            {
                kitsuId ??= await _mappingService.GetKitsuIdByAnilistIdAsync(anilistId);
                externalId = kitsuId.HasValue ? $"{kitsuPrefix}{kitsuId}" : $"{anilistPrefix}{id}";
            }

            var anime = new Meta
            {
                id = externalId,
                malId = malId,
                imdbId = imdbId,
                type = (string)result.format == "MOVIE" ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = result.title.english,
                poster = result.coverImage.extraLarge,
                descriptionRich = result.description,
                genres = result.genres.ToObject<List<string>>(),
                background = result.coverImage.extraLarge,
                videos = result.streamingEpisodes.ToObject<List<Video>>()
            };

            if (result.trailer != null && result.trailer.site == "youtube")
            {
                anime.trailers.Add(new Trailer(result.trailer.id));
                anime.trailerStreams.Add(new TrailerStream(result.trailer.id, anime.name));
            }
            for (int i = 0; i < anime.videos.Count; i++)
            {
                anime.videos[i].id = $"{externalId}:{i + 1}";
                anime.videos[i].episode = (i + 1).ToString();
                anime.videos[i].season = "1";
            }

            return anime;
        }

        public async Task UpdateEpisodeProgressAsync(TokenData tokenData, string animeId, int episode)
        {
            if (string.IsNullOrEmpty(animeId)) return;

            // Convert Kitsu ID to AniList ID if needed
            if (animeId.StartsWith(kitsuPrefix))
            {
                var kitsuIdStr = animeId.Replace(kitsuPrefix, "");
                if (!int.TryParse(kitsuIdStr, out var kitsuId)) return;
                var anilistId = await _mappingService.GetAnilistIdByKitsuIdAsync(kitsuId);
                if (!anilistId.HasValue) return;
                animeId = anilistId.Value.ToString();
            }
            else
            {
                animeId = animeId.Replace(anilistPrefix, "");
            }

            if (!int.TryParse(animeId, out var mediaId)) return;

            //// Fetch total episode count to determine if this completes the series
            //int? totalEpisodes = await GetTotalEpisodesAsync(tokenData, mediaId);
            //bool isCompleted = totalEpisodes.HasValue && totalEpisodes.Value > 0 && episode >= totalEpisodes.Value;

            var requestBody = SerializeObject(new
            {
                query = @"
                    mutation ($mediaId: Int, $progress: Int, $status: MediaListStatus) {
                        SaveMediaListEntry(mediaId: $mediaId, progress: $progress) {
                            id
                            progress
                        }
                    }",
                variables = new
                {
                    mediaId,
                    progress = episode,
                    //status = isCompleted ? "COMPLETED" : "CURRENT"
                }
            });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

            var client = _clientFactory.CreateClient();
            await client.SendAsync(request);
        }

        private async Task<int?> GetTotalEpisodesAsync(TokenData tokenData, int mediaId)
        {
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            episodes
                        }
                    }",
                variables = new { id = mediaId }
            });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var result = DeserializeObject<dynamic>(content);

            return (int?)result?.data?.Media?.episodes;
        }
    }
}

