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
        private static readonly HashSet<ListType> _userLists =
        [
            ListType.Current, ListType.Completed,
            ListType.Planning, ListType.Paused, ListType.Dropped, ListType.Repeating,
        ];

        public AnilistService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService, IKitsuService kitsuService)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _kitsuService = kitsuService;
        }

        private const int CatalogPageSize = 50;

        private string GetAnimeListQuery(TokenData tokenData, ListType? list, string skip = null, string resolvedAnimeId = null, string genre = null, string search = null, string sort = null)
        {
            var requestBody = string.Empty;

            if (list == ListType.Search)
            {
                var page = int.TryParse(skip, out var skipInt) ? (skipInt / CatalogPageSize) + 1 : 1;

                requestBody = SerializeObject(new
                {
                    query = @"
                    query ($search: String, $page: Int, $perPage: Int) {
                        Page (page: $page, perPage: $perPage) {
                            media(search: $search, type: ANIME) {
                                id
                                format
                                title {
                                    english
                                    romaji
                                }
                                coverImage {
                                    large
                                }
                                description
                            }
                        }
                    }",
                    variables = new { search = search ?? string.Empty, page, perPage = CatalogPageSize }
                });
            }
            else if (!list.HasValue || _userLists.Contains(list.Value))
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
                var sortValue = string.IsNullOrEmpty(sort) ? "POPULARITY_DESC" : SortToAnilist(sort);

                requestBody = SerializeObject(new
                {
                    query = $@"
                    query ($page: Int, $perPage: Int, $season: MediaSeason, $seasonYear: Int, $sort: [MediaSort]) {{
                        Page (page: $page, perPage: $perPage) {{
                            media(season: $season, seasonYear: $seasonYear, type: ANIME, sort: $sort) {{
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
                    variables = new { page, perPage = CatalogPageSize, season, seasonYear = year, sort = new[] { sortValue } }
                });
            }
            else
            {
                var page = int.TryParse(skip, out var skipInt) ? (skipInt / CatalogPageSize) + 1 : 1;

                var genreVarDecl = !string.IsNullOrEmpty(genre) ? ", $genre: String" : string.Empty;
                var genreArg = !string.IsNullOrEmpty(genre) ? ", genre: $genre" : string.Empty;

                // If the user picked a sort, honour it; otherwise fall back to the catalog's
                // default sort encoded in the ListType (e.g. TRENDING_DESC).
                var sortValue = string.IsNullOrEmpty(sort) ? GetListTypeString(list.Value, tokenData) : SortToAnilist(sort);

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
                        ? (object)new { sort = new[] { sortValue }, page, perPage = CatalogPageSize, genre }
                        : new { sort = new[] { sortValue }, page, perPage = CatalogPageSize }
                });
            }

            return requestBody;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist);
            var requestBody = GetAnimeListQuery(tokenData, list, skip, resolvedAnimeId, genre, search, sort);

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"),
            };

            if (!string.IsNullOrEmpty(tokenData?.access_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode) return [];

            var content = await response.Content.ReadAsStringAsync();
            IEnumerable<dynamic> result;
            var data = DeserializeObject<dynamic>(content).data;

            bool isUserList = !list.HasValue || _userLists.Contains(list.Value);

            if (list == ListType.Trending_Desc || list == ListType.Seasonal || list == ListType.Search)
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
                dynamic media;
                string entryId = null;
                string entryStatus = null;

                if (isUserList)
                {
                    media = entry.media;
                    entryId = entry.id;
                    entryStatus = entry.status;
                }
                else
                {
                    media = entry;
                }

                if (list == ListType.Current && (string)media.status == "NOT_YET_RELEASED") continue;

                // Filter user list entries by genre when discover-only provides a genre selection
                if (!string.IsNullOrEmpty(genre) && isUserList && media.genres != null)
                {
                    var genres = media.genres.ToObject<List<string>>();
                    if (!genres.Contains(genre)) continue;
                }

                var mapping = await _mappingService.GetAnilistMapping((string)media.id);

                var externalId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                    !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                    mapping?.KitsuId != null ? $"{kitsuPrefix}{mapping.KitsuId}" :
                    $"{anilistPrefix}{media.id}";

                var isMovie = IsMovieFormat((string)media.format);

                var meta = new Meta(media.description)
                {
                    id = externalId,
                    type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = string.IsNullOrEmpty((string)media.title.english) ? media.title.romaji : media.title.english,
                    poster = media.coverImage.large,
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
                var skipCount = int.TryParse(skip, out var s) ? s : 0;
                return seenIds.Values.Skip(skipCount).Take(CatalogPageSize).ToList();
            }

            return seenIds.Values.ToList();
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
                        tags {
                            name
                            rank
                            isAdult
                        },
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

            if (!string.IsNullOrEmpty(tokenData?.access_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var result = DeserializeObject<dynamic>(content).data.Media;

            var isMovie = IsMovieFormat((string)result.format);

            var mapping = await _mappingService.GetAnilistMapping((string)result.id);

            var externalId = (!string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                  !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                  mapping?.KitsuId != null ? $"{kitsuPrefix}{mapping.KitsuId}" :
                  $"{anilistPrefix}{result.id}");

            var anime = new Meta(result.description)
            {
                id = externalId,
                type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = string.IsNullOrEmpty((string)result.title.english) ? result.title.romaji : result.title.english,
                poster = result.coverImage.large,
                genres = result.genres.ToObject<List<string>>(),
                background = result.bannerImage,
            };

            if (result.trailer != null && result.trailer.site == "youtube")
            {
                anime.trailers.Add(new Trailer(result.trailer.id));
                anime.trailerStreams.Add(new TrailerStream(result.trailer.id, anime.name));
            }

            // Surface AniList tags as Meta links. Filter to non-adult, well-ranked tags so the
            // detail page doesn't get spammed with low-confidence themes.
            if (result.tags != null)
            {
                foreach (var tag in result.tags)
                {
                    if ((bool?)tag.isAdult == true) continue;
                    var rank = (int?)tag.rank ?? 0;
                    if (rank < 50) continue;
                    var name = (string)tag.name;
                    if (string.IsNullOrEmpty(name)) continue;
                    anime.links.Add(new Link
                    {
                        name = name,
                        category = "Tag",
                        url = $"https://anilist.co/search/anime?genres={Uri.EscapeDataString(name)}"
                    });
                }
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
                            score
                            notes
                            repeat
                            startedAt { year month day }
                            completedAt { year month day }
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
                var ml = data.MediaList;
                entry.EntryId = (string)ml.id?.ToString();
                entry.Status = (string)ml.status;
                entry.Progress = (int?)ml.progress ?? 0;
                entry.Score = (double?)ml.score;
                entry.Notes = (string)ml.notes;
                entry.RewatchCount = (int?)ml.repeat ?? 0;
                entry.StartedAt = FuzzyDateToDateTime(ml.startedAt);
                entry.FinishedAt = FuzzyDateToDateTime(ml.completedAt);
            }

            return entry;
        }

        public async Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist, season);

            if (string.IsNullOrEmpty(resolvedAnimeId)) return;

            if (string.IsNullOrEmpty(status))
            {
                var meta = (await GetAnimeListAsync(tokenData, animeId: $"{anilistPrefix}{resolvedAnimeId}")).FirstOrDefault();
                status = meta?.entryStatus;
            }

            // Build the variables dict so we can omit fields the caller didn't set.
            // Anything in variables ends up in the mutation payload; anything NOT in variables
            // is left server-side at its previous value (AniList only updates fields it sees).
            var variables = new Dictionary<string, object>
            {
                ["mediaId"] = resolvedAnimeId,
                ["progress"] = progress,
            };
            if (!string.IsNullOrEmpty(status)) variables["status"] = status;
            if (score.HasValue) variables["score"] = score.Value;
            if (notes != null) variables["notes"] = notes;
            if (rewatchCount.HasValue) variables["repeat"] = rewatchCount.Value;
            if (startedAt.HasValue) variables["startedAt"] = ToFuzzyDate(startedAt.Value);
            if (finishedAt.HasValue) variables["completedAt"] = ToFuzzyDate(finishedAt.Value);

            // The mutation declares only the variables we're actually sending so AniList knows the
            // schema. Optional fields are omitted from the variable declaration when null.
            var declParts = new List<string> { "$mediaId: Int", "$progress: Int" };
            if (variables.ContainsKey("status")) declParts.Add("$status: MediaListStatus");
            if (variables.ContainsKey("score")) declParts.Add("$score: Float");
            if (variables.ContainsKey("notes")) declParts.Add("$notes: String");
            if (variables.ContainsKey("repeat")) declParts.Add("$repeat: Int");
            if (variables.ContainsKey("startedAt")) declParts.Add("$startedAt: FuzzyDateInput");
            if (variables.ContainsKey("completedAt")) declParts.Add("$completedAt: FuzzyDateInput");

            var argParts = new List<string> { "mediaId: $mediaId", "progress: $progress" };
            if (variables.ContainsKey("status")) argParts.Add("status: $status");
            if (variables.ContainsKey("score")) argParts.Add("score: $score");
            if (variables.ContainsKey("notes")) argParts.Add("notes: $notes");
            if (variables.ContainsKey("repeat")) argParts.Add("repeat: $repeat");
            if (variables.ContainsKey("startedAt")) argParts.Add("startedAt: $startedAt");
            if (variables.ContainsKey("completedAt")) argParts.Add("completedAt: $completedAt");

            var query = $@"
                mutation ({string.Join(", ", declParts)}) {{
                    SaveMediaListEntry({string.Join(", ", argParts)}) {{ id }}
                }}";

            var requestBody = SerializeObject(new { query, variables });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

            var client = _clientFactory.CreateClient();
            await client.SendAsync(request);
        }

        public async Task<List<StreamingLink>> GetExternalLinksAsync(string animeId, TokenData tokenData)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist);
            if (string.IsNullOrEmpty(resolvedAnimeId)) return [];

            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            externalLinks { site url type }
                        }
                    }",
                variables = new { id = resolvedAnimeId }
            });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(tokenData?.access_token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];

            var content = await response.Content.ReadAsStringAsync();
            var media = DeserializeObject<dynamic>(content)?.data?.Media;
            if (media?.externalLinks == null) return [];

            var result = new List<StreamingLink>();
            foreach (var link in media.externalLinks)
            {
                if ((string)link.type != "STREAMING") continue;
                var site = (string)link.site;
                var url = (string)link.url;
                if (string.IsNullOrEmpty(url)) continue;
                result.Add(new StreamingLink { Site = site, Url = url });
            }
            return result;
        }

        private static DateTime? FuzzyDateToDateTime(dynamic fuzzy)
        {
            if (fuzzy == null) return null;
            int? y = (int?)fuzzy.year, m = (int?)fuzzy.month, d = (int?)fuzzy.day;
            if (!y.HasValue || !m.HasValue || !d.HasValue) return null;
            try { return new DateTime(y.Value, m.Value, d.Value); }
            catch { return null; }
        }

        private static object ToFuzzyDate(DateTime dt) => new { year = dt.Year, month = dt.Month, day = dt.Day };
    }
}

