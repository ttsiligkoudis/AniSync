using AnimeList.Models;
using AnimeList.Services.Interfaces;
using System.Collections;
using System.Formats.Tar;
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

        private string GetAnimeListQuery(TokenData tokenData, ListType? list, string skip = null, string resolvedAnimeId = null, string genre = null)
        {
            var requestBody = string.Empty;

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
                                id
                                status
                                media {{
                                    id
                                    format
                                    status
                                    genres
                                    title {{
                                        english
                                        romaji
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
                                            genres
                                            title {{
                                                english
                                                romaji
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
            else if (list == ListType.Seasonal)
            {
                var page = int.TryParse(skip, out var skipInt) ? (skipInt / CatalogPageSize) + 1 : 1;
                var (season, year) = GetSeasonAndYear(genre ?? SeasonCurrent);

                requestBody = SerializeObject(new
                {
                    query = $@"
                    query ($page: Int, $perPage: Int, $season: MediaSeason, $seasonYear: Int) {{
                        Page (page: $page, perPage: $perPage) {{
                            media(season: $season, seasonYear: $seasonYear, type: ANIME, sort: [POPULARITY_DESC]) {{
                                id
                                format
                                title {{
                                    english
                                    romaji
                                }}
                                coverImage {{
                                    large
                                }}
                                description
                            }}
                        }}
                    }}",
                    variables = !string.IsNullOrEmpty(genre)
                        ? (object)new { page, perPage = CatalogPageSize, season, seasonYear = year, genre }
                        : new { page, perPage = CatalogPageSize, season, seasonYear = year }
                });
            }
            else
            {
                var page = int.TryParse(skip, out var skipInt) ? (skipInt / CatalogPageSize) + 1 : 1;

                var genreVarDecl = !string.IsNullOrEmpty(genre) ? ", $genre: String" : string.Empty;
                var genreArg = !string.IsNullOrEmpty(genre) ? ", genre: $genre" : string.Empty;

                requestBody = SerializeObject(new
                {
                    query = $@"
                    query ($sort: [MediaSort], $page: Int, $perPage: Int{genreVarDecl}) {{
                        Page (page: $page, perPage: $perPage) {{
                            media(sort: $sort, type: ANIME{genreArg}) {{
                                id
                                title {{
                                    english
                                    romaji
                                }}
                                coverImage {{
                                    large
                                }}
                                description
                            }}
                        }}
                    }}",
                    variables = !string.IsNullOrEmpty(genre)
                        ? (object)new { sort = new List<string> { GetListTypeString(list.Value, tokenData) }, page, perPage = CatalogPageSize, genre }
                        : new { sort = new List<string> { GetListTypeString(list.Value, tokenData) }, page, perPage = CatalogPageSize }
                });
            }

            return requestBody;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist);
            var requestBody = GetAnimeListQuery(tokenData, list, skip, resolvedAnimeId, genre);

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

            if (list == ListType.Trending_Desc || list == ListType.Seasonal)
                result = data.Page.media;
            else if (!string.IsNullOrEmpty(resolvedAnimeId))
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
                string entryId = null;
                string entryStatus = null;

                if (!list.HasValue || _userLists.Contains(list.Value))
                {
                    tmpEntry = entry.media;
                    entryId = entry.id;
                    entryStatus = entry.status;
                }

                if (list == ListType.Current && (string)tmpEntry.status == "NOT_YET_RELEASED") continue;

                // Filter user list entries by genre when discover-only provides a genre selection
                if (!string.IsNullOrEmpty(genre) && isUserList && tmpEntry.genres != null)
                {
                    var genres = tmpEntry.genres.ToObject<List<string>>();
                    if (!genres.Contains(genre)) continue;
                }

                var mapping = await _mappingService.GetAnilistMapping((string)tmpEntry.id);

                var externalId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                                 !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                                 mapping?.KitsuId != null ? $"{kitsuPrefix}{mapping.KitsuId}" :
                                 $"{anilistPrefix}{tmpEntry.id}";

                var meta = new Meta
                {
                    id = externalId,
                    type = IsMovieFormat((string)tmpEntry.format) ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = string.IsNullOrEmpty((string)tmpEntry.title.english) ? tmpEntry.title.romaji : tmpEntry.title.english,
                    poster = tmpEntry.coverImage.large,
                    descriptionRich = tmpEntry.description,
                    entryId = entryId,
                    entryStatus = entryStatus
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
            if (isUserList && string.IsNullOrEmpty(resolvedAnimeId))
            {
                int skipCount = int.TryParse(skip, out var s) ? s : 0;
                return seenIds.Select(s => s.Value).Skip(skipCount).Take(CatalogPageSize).ToList();
            }

            return seenIds.Select(s => s.Value).ToList();
        }

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(id, AnimeService.Anilist);

            if (string.IsNullOrEmpty(resolvedAnimeId)) return null;

            var query = @"
                query ($id: Int) {
                    Media(id: $id) {
                        id
                        format
                        title {
                            english
                            romaji
                        }
                        bannerImage
                        coverImage {
                            large
                        }
                        description,
                        genres,
                        trailer {
                            id,
                            site
                        },
                        relations {
                          edges {
                            relationType
                            node {
                              id
                            }
                          }
                        }
                        streamingEpisodes {
                            title,
                            thumbnail
                        }
                    }
                }
            ";

            var variables = new { id = resolvedAnimeId };

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

            var mapping = await _mappingService.GetAnilistMapping((string)result.id);

            var externalId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                             !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                             mapping?.KitsuId != null ? $"{kitsuPrefix}{mapping.KitsuId}" :
                             $"{anilistPrefix}{result.id}";

            var isMovie = IsMovieFormat((string)result.format);
            var anime = new Meta
            {
                id = externalId,
                type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = string.IsNullOrEmpty((string)result.title.english) ? result.title.romaji : result.title.english,
                poster = result.coverImage.large,
                descriptionRich = result.description,
                genres = result.genres.ToObject<List<string>>(),
                background = result.bannerImage,
            };

            if (result.trailer != null && result.trailer.site == "youtube")
            {
                anime.trailers.Add(new Trailer(result.trailer.id));
                anime.trailerStreams.Add(new TrailerStream(result.trailer.id, anime.name));
            }

            if (!isMovie)
            {
                anime.videos = result.streamingEpisodes.ToObject<List<Video>>();

                var seasonNumber = GetSeasonNumber(result.relations, (int)result.id);

                for (int i = 0; i < anime.videos.Count; i++)
                {
                    anime.videos[i].id = $"{externalId}:{i + 1}";
                    anime.videos[i].episode = (i + 1);
                    anime.videos[i].season = ((int?)seasonNumber ?? 1);
                }

                if (anime.videos.Count == 0 && mapping?.KitsuId != null)
                {
                    var kitsuAnime = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", null);
                    anime.videos = kitsuAnime.videos;
                }
            }

            return anime;
        }

        private int GetSeasonNumber(dynamic relations, int animeId)
        {
            int season = 1;
            int currentId = animeId;

            var visited = new HashSet<int>(); // prevent infinite loops

            var prequels = new List<Edge>();

            if (relations?.edges != null) {
                prequels = relations.edges.ToObject<List<Edge>>();
            }

            while (true)
            {
                if (visited.Contains(currentId))
                    break;

                visited.Add(currentId);

                var prequel = prequels?.FirstOrDefault(e => e.relationType == "PREQUEL");

                if (prequel == null)
                    break;

                season++;
                currentId = prequel.node.id;
            }

            return season;
        }

        public async Task UpdateEpisodeProgressAsync(TokenData tokenData, string animeId, int season, int episode)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist, season);

            if (string.IsNullOrEmpty(resolvedAnimeId)) return;

            animeId = $"{anilistPrefix}{resolvedAnimeId}";

            var meta = (await GetAnimeListAsync(tokenData, animeId: animeId)).FirstOrDefault();

            if (string.IsNullOrEmpty(meta?.entryId)) return;

            //// Fetch total episode count to determine if this completes the series
            //int? totalEpisodes = await GetTotalEpisodesAsync(tokenData, resolvedAnimeId);
            //bool isCompleted = totalEpisodes.HasValue && totalEpisodes.Value > 0 && episode >= totalEpisodes.Value;

            var requestBody = SerializeObject(new
            {
                query = @"
                    mutation ($listEntryId: Int, $mediaId: Int, $progress: Int, $status: MediaListStatus) {
                        SaveMediaListEntry(id: $listEntryId, mediaId: $mediaId, progress: $progress, status: $status) {
                            id
                            progress
                            status
                        }
                    }",
                variables = new
                {
                    listEntryId = meta.entryId,
                    mediaId = resolvedAnimeId,
                    progress = episode,
                    status = meta.entryStatus //GetListTypeString(isCompleted ? ListType.Completed : ListType.Current, tokenData)
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

        private async Task<int?> GetTotalEpisodesAsync(TokenData tokenData, string resolvedAnimeId)
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

        public async Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist, season);

            if (string.IsNullOrEmpty(resolvedAnimeId)) return null;

            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($userId: Int, $mediaId: Int) {
                        MediaList(userId: $userId, mediaId: $mediaId, type: ANIME) {
                            id
                            status
                            progress
                        }
                        Media(id: $mediaId, type: ANIME) {
                            episodes
                        }
                    }",
                variables = new { userId = tokenData?.user_id, mediaId = resolvedAnimeId }
            });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData?.access_token);

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var data = DeserializeObject<dynamic>(content)?.data;

            var entry = new AnimeEntry
            {
                MediaId = resolvedAnimeId,
                TotalEpisodes = (int?)data?.Media?.episodes
            };

            if (data?.MediaList != null)
            {
                entry.EntryId = (string)data.MediaList.id?.ToString();
                entry.Status = (string)data.MediaList.status;
                entry.Progress = (int?)data.MediaList.progress ?? 0;
            }

            return entry;
        }

        public async Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, string status, int progress)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist, season);

            if (string.IsNullOrEmpty(resolvedAnimeId)) return;

            var requestBody = SerializeObject(new
            {
                query = @"
                    mutation ($mediaId: Int, $progress: Int, $status: MediaListStatus) {
                        SaveMediaListEntry(mediaId: $mediaId, progress: $progress, status: $status) {
                            id
                            progress
                            status
                        }
                    }",
                variables = new
                {
                    mediaId = resolvedAnimeId,
                    progress,
                    status
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
    }
}

