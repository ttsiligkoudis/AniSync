using AnimeList.Models;
using AnimeList.Services.Interfaces;
using System.Net.Http.Headers;

namespace AnimeList.Services
{
    public class AnilistService : IAnilistService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly IKitsuService _kitsuService;
        private readonly string _anilistApi = "https://graphql.anilist.co";
        private readonly List<ListType> _userLists = [ListType.Current, ListType.Completed ];

        public AnilistService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService, IKitsuService kitsuService)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _kitsuService = kitsuService;
        }

        private const int CatalogPageSize = 50;

        private string GetAnimeListQuery(TokenData tokenData, ListType? list, string skip = null, int? resolvedAnimeId = null)
        {
            var requestBody = string.Empty;

            if (!list.HasValue || _userLists.Contains(list.Value))
            {
                if (resolvedAnimeId.HasValue)
                {
                    var statusArg = list.HasValue ? $", status: {GetListTypeString(list.Value, tokenData)}" : string.Empty;

                    requestBody = SerializeObject(new
                    {
                        query = $@"
                        query ($userId: Int, $mediaId: Int) {{
                            MediaList(userId: $userId, mediaId: $mediaId, type: ANIME{statusArg}) {{
                                media {{
                                    id
                                    format
                                    status
                                    title {{
                                        english
                                    }}
                                    coverImage {{
                                        medium
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
                    var statusArg = list.HasValue ? $", status: {GetListTypeString(list.Value, tokenData)}" : string.Empty;

                    requestBody = SerializeObject(new
                    {
                        query = $@"
                        query ($userId: Int) {{
                            MediaListCollection(userId: $userId, type: ANIME{statusArg}) {{
                                lists {{
                                    entries {{
                                        media {{
                                            id
                                            format
                                            status
                                            title {{
                                                english
                                            }}
                                            coverImage {{
                                                medium
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
                var page = int.TryParse(skip, out var skipInt) ? (skipInt / CatalogPageSize) + 1 : 1;
                var mediaIdArg = resolvedAnimeId.HasValue ? ", id: $mediaId" : string.Empty;
                var query = """
                    query ($sort: [MediaSort], $mediaId: Int, $page: Int, $perPage: Int) {
                        Page (page: $page, perPage: $perPage) {
                            media(sort: $sort, type: ANIME__MEDIA_ID_ARG__) {
                                id
                                format
                                title {
                                    english
                                }
                                coverImage {
                                    medium
                                }
                                description
                            }
                        }
                    }
                    """.Replace("__MEDIA_ID_ARG__", mediaIdArg);

                requestBody = SerializeObject(new
                {
                    query,
                    variables = new { sort = new List<string> { GetListTypeString(list.Value, tokenData) }, mediaId = resolvedAnimeId, page, perPage = CatalogPageSize }
                });
            }

            return requestBody;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist);
            var requestBody = GetAnimeListQuery(tokenData, list, skip, resolvedAnimeId);

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

            bool isUserList = !list.HasValue || _userLists.Contains(list.Value);

            if (list == ListType.Trending_Desc)
                result = data.Page.media;
            else if (resolvedAnimeId.HasValue)
                result = data.MediaList == null ? Array.Empty<dynamic>() : [data.MediaList];
            else
            {
                // MediaListCollection groups entries by status lists; flatten them
                var entries = new List<dynamic>();
                foreach (var lst in data.MediaListCollection.lists)
                    foreach (var entry in lst.entries)
                        entries.Add(entry);
                result = entries;
            }

            // Ensure mapping cache is loaded once, so the lookups below are pure dictionary reads
            await _mappingService.EnsureLoadedAsync();

            var seenIds = new Dictionary<string, Meta>();
            foreach (var entry in result)
            {
                var tmpEntry = entry;
                if (list != ListType.Trending_Desc) tmpEntry = entry.media;
                if (list == ListType.Current && (string)tmpEntry.status == "NOT_YET_RELEASED") continue;

                int anilistId = (int)tmpEntry.id;
                string externalId = anilistId > 0 ? await _mappingService.GetImdbIdByAnilistIdAsync(anilistId) : null;

                // Fall back to Kitsu ID when IMDb is unavailable (Torrentio supports Kitsu IDs)
                if (string.IsNullOrEmpty(externalId))
                {
                    int? kitsuId = await _mappingService.GetKitsuIdByAnilistIdAsync(anilistId);
                    externalId = kitsuId.HasValue ? $"{kitsuPrefix}{kitsuId}" : $"{anilistPrefix}{anilistId}";
                }

                var meta = new Meta
                {
                    id = externalId,
                    type = (string)tmpEntry.format == "MOVIE" ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = (string)tmpEntry.title.english,
                    poster = tmpEntry.coverImage.medium,
                    descriptionRich = tmpEntry.description,
                };

                // Multiple AniList entries (seasons/OVAs) can share the same IMDb ID;
                // keep the shortest English title as it's typically the base series name
                if (seenIds.TryGetValue(externalId, out var existing))
                {
                    if (!string.IsNullOrEmpty(meta.name) && (string.IsNullOrEmpty(existing.name) || meta.name.Length < existing.name.Length))
                        seenIds[externalId] = meta;

                    continue;
                }

                seenIds[externalId] = meta;
            }

            // User lists are fetched in full via MediaListCollection; paginate after dedup
            if (isUserList && !resolvedAnimeId.HasValue)
            {
                int skipCount = int.TryParse(skip, out var s) ? s : 0;
                return seenIds.Select(s => s.Value).Skip(skipCount).Take(CatalogPageSize).ToList();
            }

            return seenIds.Select(s => s.Value).ToList();
        }

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(id, AnimeService.Anilist);

            if (!resolvedAnimeId.HasValue) return null;

            var query = @"
                query ($id: Int) {
                    Media(id: $id) {
                        id
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

            var variables = new { id = resolvedAnimeId.Value.ToString() };

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

            int anilistId = (int)result.id;
            string externalId = anilistId > 0 ? await _mappingService.GetImdbIdByAnilistIdAsync(anilistId) : null;

            int? kitsuId = null;

            // Fall back to Kitsu ID when IMDb is unavailable (Torrentio supports Kitsu IDs)
            if (string.IsNullOrEmpty(externalId))
            {
                kitsuId = await _mappingService.GetKitsuIdByAnilistIdAsync(anilistId);
                externalId = kitsuId.HasValue ? $"{kitsuPrefix}{kitsuId}" : $"{anilistPrefix}{id}";
            }

            var anime = new Meta
            {
                id = externalId,
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

            if (!anime.videos.Any() && kitsuId.HasValue)
            {
                var kitsuAnime = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{kitsuId}", null);
                anime.videos = kitsuAnime.videos;
            }

            return anime;
        }

        public async Task UpdateEpisodeProgressAsync(TokenData tokenData, string animeId, int episode)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist);

            if (!resolvedAnimeId.HasValue) return;

            //// Fetch total episode count to determine if this completes the series
            //int? totalEpisodes = await GetTotalEpisodesAsync(tokenData, resolvedAnimeId.Value);
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
                    mediaId = resolvedAnimeId.Value,
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

        private async Task<int?> GetTotalEpisodesAsync(TokenData tokenData, int resolvedAnimeId)
        {
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            episodes
                        }
                    }",
                variables = new { id = resolvedAnimeId }
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

