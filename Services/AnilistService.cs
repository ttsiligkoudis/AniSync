using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;

namespace AnimeList.Services
{
    public class AnilistService : IAnilistService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly IKitsuService _kitsuService;
        private readonly IAnilistFallback _anilistFallback;
        private readonly ICinemetaService _cinemetaService;
        private readonly string _anilistApi = "https://graphql.anilist.co";
        private static readonly HashSet<ListType> _userLists =
        [
            ListType.Current, ListType.Completed,
            ListType.Planning, ListType.Paused, ListType.Dropped, ListType.Repeating,
        ];

        public AnilistService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService, IKitsuService kitsuService, IAnilistFallback anilistFallback, ICinemetaService cinemetaService)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _kitsuService = kitsuService;
            _anilistFallback = anilistFallback;
            _cinemetaService = cinemetaService;
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

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null, bool groupSeasons = true)
        {
            // Airing schedule is shared between services and lives in the cross-service helper
            if (list == ListType.Airing)
                return await _anilistFallback.GetAiringScheduleAsync(AnimeService.Anilist, skip);

            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist);
            var requestBody = GetAnimeListQuery(tokenData, list, skip, resolvedAnimeId, genre, search, sort);

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"),
            };

            ApplyBearerAuth(request, tokenData);

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

                // groupSeasons=true → fall through to IMDb / TMDB / Kitsu before the native
                // AniList id so multiple cours of a franchise collapse to one card via the
                // dedup step below. When the user disables grouping, every cour gets its own
                // native id and dedup is a no-op since native ids don't collide.
                var (externalId, _, _) = ResolveGroupedId(
                    mapping, $"{anilistPrefix}{media.id}", groupSeasons, allowKitsuFallback: true);

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

            // User-list catalogs no longer carry a `skip` extra in the manifest, so Stremio
            // asks for the whole list in one round-trip — return the full deduped collection
            // here instead of paginating it. Discovery catalogs (Trending/Seasonal/Search/
            // Airing) still paginate via the API's own page mechanism above.
            //
            // Sort user libraries alphabetically by name so franchise cours sit next to each
            // other ("Show", "Show Part 2", "Show Season 2", …) — applies regardless of the
            // groupSeasons toggle. Discovery catalogs preserve the API's ranking.
            if (isUserList)
                return seenIds.Values
                    .OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            return seenIds.Values.ToList();
        }

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData, bool groupSeasons = true)
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
                        studios {
                            edges {
                                isMain
                                node { name siteUrl }
                            }
                        },
                        staff(sort: RELEVANCE, perPage: 10) {
                            edges {
                                role
                                node {
                                    name { full }
                                    siteUrl
                                }
                            }
                        },
                        recommendations(sort: RATING_DESC, perPage: 15) {
                            edges {
                                node {
                                    mediaRecommendation {
                                        id
                                        title { english romaji }
                                    }
                                }
                            }
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
                        episodes
                    }
                }
            ";

            var variables = new { id = resolvedAnimeId };

            var ser = SerializeObject(new { query, variables });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(ser, System.Text.Encoding.UTF8, "application/json")
            };

            ApplyBearerAuth(request, tokenData);

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

            // Same toggle as the catalog path: when grouping is on, prefer cross-service ids
            // so meta.id matches what the user clicked from a grouped catalog. When off, keep
            // the response in this service's native id space.
            // videoId will still use the groupId since it is better source for streams.
            var (externalId, groupId, hasGroupId) = ResolveGroupedId(
                mapping, $"{anilistPrefix}{result.id}", groupSeasons, allowKitsuFallback: true);

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

            if (result.studios?.edges != null)
            {
                foreach (var edge in result.studios.edges)
                {
                    if ((bool?)edge.isMain != true) continue; // skip licensors / sub-studios
                    var name = (string)edge.node?.name;
                    var siteUrl = (string)edge.node?.siteUrl;
                    if (string.IsNullOrEmpty(name)) continue;
                    anime.links.Add(new Link { name = name, category = "Studio", url = siteUrl });
                }
            }

            if (result.staff?.edges != null)
            {
                foreach (var edge in result.staff.edges)
                {
                    var role = (string)edge.role;
                    var name = (string)edge.node?.name?.full;
                    var siteUrl = (string)edge.node?.siteUrl;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(role)) continue;

                    // Map to Stremio's standard link categories where the role is recognisable
                    var category = StaffRoleToCategory(role);
                    anime.links.Add(new Link { name = name, category = category, url = siteUrl });
                }
            }

            if (result.recommendations?.edges != null)
            {
                foreach (var edge in result.recommendations.edges)
                {
                    var rec = edge.node?.mediaRecommendation;
                    if (rec == null) continue;
                    var recId = (int?)rec.id;
                    if (!recId.HasValue) continue;
                    var name = string.IsNullOrEmpty((string)rec.title?.english)
                        ? (string)rec.title?.romaji
                        : (string)rec.title?.english;
                    if (string.IsNullOrEmpty(name)) continue;

                    anime.links.Add(new Link
                    {
                        name = name,
                        category = "Similar",
                        url = $"https://anilist.co/anime/{recId.Value}",
                    });
                }
            }

            if (!isMovie)
            {
                // Prefer Cinemeta when we have an IMDb mapping — its per-episode coverage
                // is richer than AniList's streamingEpisodes (which only carries entries for
                // episodes that aired on a partner streaming service). try/catch because a
                // malformed mapping (Season parser, etc.) shouldn't take the meta page down.
                if (!string.IsNullOrEmpty(mapping?.ImdbId))
                {
                    try
                    {
                        var anilistIdInt = (int)result.id;
                        var currentEpisodeCount = (int?)result.episodes ?? 0;
                        anime.videos = await _cinemetaService.GetCourEpisodesAsync(
                            mapping.ImdbId, mapping.Season, AnimeService.Anilist,
                            anilistIdInt, currentEpisodeCount, GetAnimeSummaryAsync);
                    }
                    catch
                    {
                        anime.videos = new List<Video>();
                    }
                }

                if (anime.videos.Count == 0)
                {
                    // streamingEpisodes can be null on AniList for anime that haven't aired yet
                    // or simply lack the data — ToObject on a JTokenType.Null returns null, and
                    // accessing the field at all on a JObject returns null for missing keys.
                    // Default to an empty list so the iteration / fallback below is NRE-safe.
                    JToken streamingEps = result.streamingEpisodes as JToken;
                    anime.videos = (streamingEps != null && streamingEps.Type != JTokenType.Null)
                        ? (streamingEps.ToObject<List<Video>>() ?? new List<Video>())
                        : new List<Video>();

                    var seasonNumber = (int?)GetSeasonNumber(result.relations, (int)result.id) ?? 1;
                    if (seasonNumber <= 0) seasonNumber = 1;

                    for (int i = 0; i < anime.videos.Count; i++)
                    {
                        anime.videos[i].episode = (i + 1);
                        anime.videos[i].season = seasonNumber;
                    }

                    if (anime.videos.Count == 0 && mapping?.KitsuId != null)
                    {
                        var kitsuAnime = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", null);
                        anime.videos = kitsuAnime?.videos ?? new List<Video>();
                    }
                }

                // Stremio rejects (renders blank) when video.id doesn't share a prefix with
                // meta.id. The Kitsu cross-service fallback above leaves kitsu:N-prefixed
                // ids in place, and the legacy synthetic loops used a :episode shape rather
                // than the :season:episode shape Stremio expects for series videos.
                NormalizeVideoIds(anime.videos, groupId, hasGroupId);
            }

            // links must be valid or stremio throws error and page can't render. 
            anime.links = anime.links.Where(w => IsValidUrl(w.url)).ToList();

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
            return await GetAnimeEntryByResolvedIdAsync(tokenData, resolvedAnimeId);
        }

        private async Task<AnimeEntry> GetAnimeEntryByResolvedIdAsync(TokenData tokenData, string resolvedAnimeId)
        {
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
            ApplyBearerAuth(request, tokenData);

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
                // AniList returns 0 for "no score" (instead of null). Coalesce so the
                // Manage Entry form shows an empty input and the sync fan-out doesn't
                // propagate a 0 that Kitsu would 422 on (ratingTwenty min is 2).
                entry.Score = NullableScore((double?)ml.score);
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

            if (string.IsNullOrEmpty(resolvedAnimeId))
            {
                Console.Error.WriteLine($"[AniList] Save skipped — no AniList mapping for animeId={animeId} season={season}.");
                return;
            }

            if (string.IsNullOrEmpty(status))
            {
                var existing = await GetAnimeEntryByResolvedIdAsync(tokenData, resolvedAnimeId);
                status = existing?.Status;
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
            ApplyBearerAuth(request, tokenData);

            var client = _clientFactory.CreateClient();
            var saveResponse = await client.SendAsync(request);
            await EnsureSuccessOrThrow(saveResponse, "AniList", "save");
        }

        public async Task DeleteAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null)
        {
            // DeleteMediaListEntry takes the MediaList id, not the media id, so fetch the
            // existing entry to get it. If the user has nothing on their list there's
            // nothing to delete and we exit silently.
            var entry = await GetAnimeEntryAsync(tokenData, animeId, season);
            if (string.IsNullOrEmpty(entry?.EntryId)
                || !int.TryParse(entry.EntryId, out var listId))
                return;

            var requestBody = SerializeObject(new
            {
                query = @"
                    mutation ($id: Int) {
                        DeleteMediaListEntry(id: $id) { deleted }
                    }",
                variables = new { id = listId },
            });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"),
            };
            ApplyBearerAuth(request, tokenData);

            var deleteResponse = await _clientFactory.CreateClient().SendAsync(request);
            await EnsureSuccessOrThrow(deleteResponse, "AniList", "delete");
        }

        public async Task<(string? name, int? episodeCount)> GetAnimeSummaryAsync(string id)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(id, AnimeService.Anilist);
            if (string.IsNullOrEmpty(resolvedAnimeId)) return (null, null);

            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            episodes
                            title { english romaji }
                        }
                    }",
                variables = new { id = resolvedAnimeId },
            });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json"),
            };

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return (null, null);

            var content = await response.Content.ReadAsStringAsync();
            var media = DeserializeObject<dynamic>(content)?.data?.Media;
            if (media == null) return (null, null);

            var name = string.IsNullOrEmpty((string)media.title?.english)
                ? (string)media.title?.romaji
                : (string)media.title?.english;
            return (name, (int?)media.episodes);
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
            ApplyBearerAuth(request, tokenData);

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

        public async Task<List<AnimeEntry>> GetUserListEntriesAsync(TokenData tokenData)
        {
            if (string.IsNullOrEmpty(tokenData?.access_token) || string.IsNullOrEmpty(tokenData.user_id))
                return [];

            // MediaListCollection returns the user's entire library in one round-trip,
            // grouped by status. Pull every per-entry field the sync fan-out needs;
            // skip the heavy media payload (genres, cover, description) since we only
            // need ids and episode counts here.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($userId: Int) {
                        MediaListCollection(userId: $userId, type: ANIME) {
                            lists {
                                entries {
                                    id
                                    status
                                    progress
                                    score
                                    notes
                                    repeat
                                    startedAt { year month day }
                                    completedAt { year month day }
                                    media { id episodes }
                                }
                            }
                        }
                    }",
                variables = new { userId = tokenData.user_id }
            });

            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            ApplyBearerAuth(request, tokenData);

            var response = await _clientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];

            var content = await response.Content.ReadAsStringAsync();
            var data = DeserializeObject<dynamic>(content)?.data;
            if (data?.MediaListCollection?.lists == null) return [];

            var result = new List<AnimeEntry>();
            foreach (var lst in data.MediaListCollection.lists)
            {
                if (lst?.entries == null) continue;
                foreach (var ml in lst.entries)
                {
                    var mediaId = (string)ml.media?.id?.ToString();
                    if (string.IsNullOrEmpty(mediaId)) continue;

                    result.Add(new AnimeEntry
                    {
                        EntryId = (string)ml.id?.ToString(),
                        // Prefix so the sync orchestrator can pass this straight through to
                        // GetIdByService for cross-service translation.
                        MediaId = $"{anilistPrefix}{mediaId}",
                        Status = (string)ml.status,
                        Progress = (int?)ml.progress ?? 0,
                        TotalEpisodes = (int?)ml.media?.episodes,
                        Score = NullableScore((double?)ml.score),
                        Notes = (string)ml.notes,
                        RewatchCount = (int?)ml.repeat ?? 0,
                        StartedAt = FuzzyDateToDateTime(ml.startedAt),
                        FinishedAt = FuzzyDateToDateTime(ml.completedAt),
                    });
                }
            }
            return result;
        }

        private static object ToFuzzyDate(DateTime dt) => new { year = dt.Year, month = dt.Month, day = dt.Day };

        /// <summary>
        /// Maps an AniList staff role string to a Stremio link category. Stremio recognises
        /// "director", "writer", "actor"; other roles fall back to a free-form label.
        /// </summary>
        private static string StaffRoleToCategory(string role)
        {
            var r = role.ToLowerInvariant();
            if (r.Contains("director")) return "director";
            if (r.Contains("writ") || r.Contains("script") || r.Contains("creator")) return "writer";
            if (r.Contains("composer") || r.Contains("music")) return "Composer";
            if (r.Contains("character design") || r.Contains("art")) return "Artist";
            if (r.Contains("producer")) return "Producer";
            return "Staff";
        }
    }
}

