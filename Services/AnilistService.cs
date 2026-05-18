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
        private readonly ILogger<AnilistService> _logger;
        private readonly string _anilistApi = "https://graphql.anilist.co";
        private static readonly HashSet<ListType> _userLists =
        [
            ListType.Current, ListType.Completed,
            ListType.Planning, ListType.Paused, ListType.Dropped, ListType.Repeating,
        ];

        public AnilistService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService, IKitsuService kitsuService, IAnilistFallback anilistFallback, ICinemetaService cinemetaService, ILogger<AnilistService> logger)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _kitsuService = kitsuService;
            _anilistFallback = anilistFallback;
            _cinemetaService = cinemetaService;
            _logger = logger;
        }

        private const int CatalogPageSize = 50;

        // Posts a serialised GraphQL body and returns the dynamic `data` payload, or
        // Returns the supplied tokenData iff it's actually an AniList account
        // token, else null so PostGraphQLAsync sends an anonymous request.
        // Critical for the public meta queries (Media-by-id, recommendations,
        // etc.) when the viewer's primary is MAL or Kitsu — those callers
        // hand us a MAL/Kitsu bearer that AniList would 401 on, and the
        // query is public so we don't need auth anyway.
        private static TokenData AnilistBearerOrAnonymous(TokenData tokenData)
        {
            if (tokenData == null) return null;
            if (tokenData.anime_service != AnimeService.Anilist) return null;
            if (string.IsNullOrWhiteSpace(tokenData.access_token)) return null;
            return tokenData;
        }

        // null on transport failure. Bearer auth is applied when tokenData carries
        // an access_token; reads with no token (e.g. anonymous summary lookups) pass
        // null.
        private async Task<dynamic> PostGraphQLAsync(string requestBody, TokenData tokenData = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            ApplyBearerAuth(request, tokenData);
            var response = await _clientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var content = await response.Content.ReadAsStringAsync();
            return DeserializeObject<dynamic>(content)?.data;
        }

        // Posts a GraphQL mutation and surfaces non-success responses via
        // EnsureSuccessOrThrow so SyncService can flag NeedsReauth on a stale token.
        private async Task PostGraphQLMutationAsync(string requestBody, TokenData tokenData, string op)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _anilistApi)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };
            ApplyBearerAuth(request, tokenData);
            var response = await _clientFactory.CreateClient().SendAsync(request);
            await EnsureSuccessOrThrow(response, "AniList", op);
        }

        private string GetAnimeListQuery(TokenData tokenData, ListType? list, string skip = null, string resolvedAnimeId = null, string genre = null, string search = null, string sort = null, string season = null, bool hideAdult = false)
        {
            // Inline filter argument appended to every media(...) clause when
            // the caller asked to hide 18+ entries. AniList filters server-
            // side via media(isAdult: false), so we don't have to post-process
            // the response.
            var adultArg = hideAdult ? ", isAdult: false" : string.Empty;
            var requestBody = string.Empty;

            if (list == ListType.Search)
            {
                var page = int.TryParse(skip, out var skipInt) ? (skipInt / CatalogPageSize) + 1 : 1;

                // Genre + season are optional layered filters on the same Page
                // query. The combined intent ("show me Naruto results in
                // Action this season") matters because the form preserves
                // every filter across submissions; ignoring genre/season
                // when search was set would silently drop the user's other
                // picks. Stitch the GraphQL signature dynamically so we only
                // declare variables we'll actually pass.
                var hasGenre = !string.IsNullOrEmpty(genre);
                var hasSeason = !string.IsNullOrEmpty(season);
                string seasonValue = null;
                int seasonYear = 0;
                if (hasSeason)
                {
                    var (sv, y) = GetSeasonAndYear(season);
                    seasonValue = sv;
                    seasonYear = y;
                }

                var genreVarDecl  = hasGenre  ? ", $genre: String"                       : string.Empty;
                var genreArg      = hasGenre  ? ", genre: $genre"                        : string.Empty;
                var seasonVarDecl = hasSeason ? ", $season: MediaSeason, $seasonYear: Int" : string.Empty;
                var seasonArg     = hasSeason ? ", season: $season, seasonYear: $seasonYear" : string.Empty;

                var variables = new JObject
                {
                    ["search"] = search ?? string.Empty,
                    ["page"] = page,
                    ["perPage"] = CatalogPageSize,
                };
                if (hasGenre) variables["genre"] = genre;
                if (hasSeason)
                {
                    variables["season"] = seasonValue;
                    variables["seasonYear"] = seasonYear;
                }

                requestBody = SerializeObject(new
                {
                    query = $@"
                    query ($search: String, $page: Int, $perPage: Int{genreVarDecl}{seasonVarDecl}) {{
                        Page (page: $page, perPage: $perPage) {{
                            media(search: $search, type: ANIME{genreArg}{seasonArg}{adultArg}) {{
                                id
                                format
                                episodes
                                averageScore
                                seasonYear
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
                    variables,
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
                                progress
                                media {{
                                    id
                                    format
                                    status
                                    genres
                                    episodes
                                    averageScore
                                    seasonYear
                                    isAdult
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
                                        id
                                        status
                                        progress
                                        media {{
                                            id
                                            format
                                            status
                                            genres
                                            episodes
                                            averageScore
                                            seasonYear
                                            isAdult
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
                // Two season-selector inputs flow in here:
                //   1. An explicit `season` param ("Spring 2026" style) from
                //      the web app's Discover season dropdown — takes
                //      precedence when set.
                //   2. The legacy `genre`-as-season-keyword overload used by
                //      the Stremio addon's catalog extras ("This Season" /
                //      "Next Season" / "Previous Season").
                // Anything else in `genre` is a real genre filter that
                // layers on top of the resolved (season, year).
                var seasonSelector = !string.IsNullOrEmpty(season)
                    ? season
                    : (!string.IsNullOrEmpty(genre) && SeasonOptions.Contains(genre) ? genre : SeasonCurrent);
                var (seasonValue, year) = GetSeasonAndYear(seasonSelector);
                var realGenre = (!string.IsNullOrEmpty(genre) && !SeasonOptions.Contains(genre)) ? genre : null;
                var hasGenreFilter = !string.IsNullOrEmpty(realGenre);

                var page = int.TryParse(skip, out var skipInt) ? (skipInt / CatalogPageSize) + 1 : 1;
                var sortValue = string.IsNullOrEmpty(sort) ? "POPULARITY_DESC" : SortToAnilist(sort);

                var genreVarDecl = hasGenreFilter ? ", $genre: String" : string.Empty;
                var genreArg = hasGenreFilter ? ", genre: $genre" : string.Empty;

                requestBody = SerializeObject(new
                {
                    query = $@"
                    query ($page: Int, $perPage: Int, $season: MediaSeason, $seasonYear: Int, $sort: [MediaSort]{genreVarDecl}) {{
                        Page (page: $page, perPage: $perPage) {{
                            media(season: $season, seasonYear: $seasonYear, type: ANIME, sort: $sort{genreArg}{adultArg}) {{
                                id
                                format
                                episodes
                                averageScore
                                seasonYear
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
                    variables = hasGenreFilter
                        ? (object)new { page, perPage = CatalogPageSize, season = seasonValue, seasonYear = year, sort = new[] { sortValue }, genre = realGenre }
                        : new { page, perPage = CatalogPageSize, season = seasonValue, seasonYear = year, sort = new[] { sortValue } }
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
                            media(sort: $sort, type: ANIME{genreArg}{adultArg}) {{
                                id
                                format
                                episodes
                                averageScore
                                seasonYear
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

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null, bool groupSeasons = true, string season = null, bool hideUnreleased = false, bool hideAdult = false)
        {
            // Airing schedule is shared between services and lives in the cross-service helper.
            // genre threads through so the Discover page's "Airing + Action" filter swaps
            // the upcoming-episode schedule for a currently-airing-in-genre listing.
            if (list == ListType.Airing)
                return await _anilistFallback.GetAiringScheduleAsync(AnimeService.Anilist, skip, genre);

            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Anilist);
            var requestBody = GetAnimeListQuery(tokenData, list, skip, resolvedAnimeId, genre, search, sort, season, hideAdult);

            var data = await PostGraphQLAsync(requestBody, tokenData);
            if (data == null) return [];

            IEnumerable<dynamic> result;

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
                int? entryProgress = null;

                if (isUserList)
                {
                    media = entry.media;
                    entryId = entry.id;
                    entryStatus = entry.status;
                    entryProgress = (int?)entry.progress;
                }
                else
                {
                    media = entry;
                }

                if (hideUnreleased && list == ListType.Current && (string)media.status == "NOT_YET_RELEASED") continue;

                // Catalog (Discover/Search/Seasonal/Trending) queries filter via
                // media(isAdult: false, …) up at the GraphQL layer, so this
                // post-check only really matters for user-list queries
                // (MediaList / MediaListCollection) where the filter doesn't
                // sit on the media selector. Cheap defensive check either way.
                if (hideAdult && (bool?)media.isAdult == true) continue;

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
                    entryStatus = entryStatus,
                    // StreamD-style card chrome: score badge + format/eps/year info row.
                    // averageScore is 0-100 on AniList; normalise to 0-10 with one decimal
                    // so the badge format is consistent across providers (MAL is already
                    // 0-10, Kitsu's averageRating is 0-100). Null values pass through and
                    // the partial omits the corresponding chunk gracefully.
                    score = media.averageScore != null ? Math.Round((double)media.averageScore / 10, 1) : (double?)null,
                    episodes = media.episodes,
                    year = media.seasonYear,
                    format = NormalizeFormat((string)media.format),
                    progress = entryProgress,
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
                        status
                        source
                        duration
                        averageScore
                        seasonYear
                        isAdult
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
                            id
                            name
                            rank
                            isAdult
                        },
                        studios {
                            edges {
                                isMain
                                node { id name siteUrl }
                            }
                        },
                        staff(sort: RELEVANCE, perPage: 10) {
                            edges {
                                role
                                node {
                                    id
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
                                        format
                                        episodes
                                        averageScore
                                        seasonYear
                                        title { english romaji }
                                        coverImage { large }
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
                              type
                              format
                              isAdult
                              title { english romaji }
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

            var data = await PostGraphQLAsync(SerializeObject(new { query, variables = new { id = resolvedAnimeId } }), AnilistBearerOrAnonymous(tokenData));
            if (data == null) return null;
            var result = data.Media;

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
                // Same fields the catalog Meta builder populates so /anime/{id}
                // and the cards render consistent chrome (community score
                // badge, format · X eps · year info row).
                score = result.averageScore != null ? Math.Round((double)result.averageScore / 10, 1) : (double?)null,
                episodes = (int?)result.episodes,
                year = (int?)result.seasonYear,
                format = NormalizeFormat((string)result.format),
                airStatus = NormalizeAirStatus((string)result.status),
                source = NormalizeSource((string)result.source),
                avgDuration = (int?)result.duration,
                isAdult = (bool?)result.isAdult ?? false,
            };

            // Tags subselection is already fetched (rank + isAdult). Filter
            // out adult-only tags and rank-50-or-below noise; keep the top
            // 8 by rank for the detail page's themes strip.
            if (result.tags != null)
            {
                var topTags = new List<(string name, int rank)>();
                foreach (var tag in result.tags)
                {
                    var tagName = (string)tag.name;
                    var tagRank = (int?)tag.rank ?? 0;
                    var tagAdult = (bool?)tag.isAdult ?? false;
                    if (string.IsNullOrEmpty(tagName) || tagAdult || tagRank < 50) continue;
                    topTags.Add((tagName, tagRank));
                }
                anime.tags = topTags
                    .OrderByDescending(t => t.rank)
                    .Take(8)
                    .Select(t => t.name)
                    .ToList();
            }

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
                        // Detail-page chip wires this directly into the
                        // in-app /discover?tag= filter, so the external url
                        // is only used as a fallback for any service path
                        // that doesn't expose an AniList tag link.
                        url = $"https://anilist.co/search/anime?genres={Uri.EscapeDataString(name)}",
                        anilistId = (long?)tag.id,
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
                    anime.links.Add(new Link
                    {
                        name = name,
                        category = "Studio",
                        url = siteUrl,
                        anilistId = (long?)edge.node?.id,
                    });
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
                    anime.links.Add(new Link
                    {
                        name = name,
                        category = category,
                        url = siteUrl,
                        anilistId = (long?)edge.node?.id,
                    });
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

                    // Two outputs per recommendation:
                    //   1. The Stremio-side "Similar" Link (kept for the addon's
                    //      meta JSON consumers — Stremio uses these for "More
                    //      like this" navigation in the addon flow).
                    //   2. A slim Meta in anime.recommendations for the web
                    //      app's detail-page carousel. Same id / name with
                    //      poster + score + format + year + episodes pulled
                    //      from the extended GraphQL subselection.
                    anime.links.Add(new Link
                    {
                        name = name,
                        category = "Similar",
                        url = $"https://anilist.co/anime/{recId.Value}",
                    });

                    anime.recommendations.Add(new Meta
                    {
                        id = $"{anilistPrefix}{recId.Value}",
                        name = name,
                        poster = (string)rec.coverImage?.large,
                        type = IsMovieFormat((string)rec.format)
                            ? MetaType.movie.ToString()
                            : MetaType.series.ToString(),
                        score = rec.averageScore != null
                            ? Math.Round((double)rec.averageScore / 10, 1)
                            : (double?)null,
                        episodes = (int?)rec.episodes,
                        year = (int?)rec.seasonYear,
                        format = NormalizeFormat((string)rec.format),
                    });
                }
            }

            // Relations → Stremio meta links + an informational
            // "Related" section appended to the description.
            //
            // Stremio web silently drops links whose URL uses the
            // stremio:/// scheme — observed against the current build
            // where the Tag / Studio / Staff / Similar chips render
            // (all https URLs) but Prequel / Sequel chips emitted with
            // stremio:/// URLs do not. Switching the chip URL to
            // https://web.stremio.com/#/detail/{type}/{id} threads the
            // needle: Stremio web's link filter accepts the https
            // scheme and renders the chip; the native Stremio apps
            // (desktop / mobile) intercept web.stremio.com via
            // Universal Links / App Links and open the meta in-app.
            // Worst case on a native client where the interception
            // isn't registered, the URL opens in the user's browser
            // which loads Stremio web — still a working navigation.
            //
            // We mirror the list into the description so mobile users
            // (where the chip row is silently dropped entirely, even
            // for https URLs) at least SEE which related entries
            // exist — description is plain text with no auto-linkify,
            // so the lines aren't clickable, but the user can tap the
            // chips on web/desktop or type the title into search on
            // mobile.
            //
            // Filter: PREQUEL / SEQUEL only — matches the set
            // AnilistFallback.GetRelatedAsync emits to the web app's
            // /anime/{id} "Related" carousel, so the two surfaces stay
            // in sync. ANIME-only; manga relations would 404 on the
            // meta route. Adult relations dropped — AnimeController's
            // detail gate already 404s the click, so a dead chip just
            // confuses the user.
            if (result.relations?.edges != null)
            {
                var relLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PREQUEL"] = "Prequel",
                    ["SEQUEL"]  = "Sequel",
                };
                var descriptionExtras = new List<string>();
                foreach (var edge in result.relations.edges)
                {
                    var relType = (string)edge.relationType;
                    if (relType == null || !relLabels.TryGetValue(relType, out var label)) continue;
                    var node = edge.node;
                    if (node == null) continue;
                    var relId = (int?)node.id;
                    if (!relId.HasValue) continue;
                    if ((bool?)node.isAdult == true) continue;
                    var nodeType = (string)node.type;
                    if (!string.Equals(nodeType, "ANIME", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = string.IsNullOrEmpty((string)node.title?.english)
                        ? (string)node.title?.romaji
                        : (string)node.title?.english;
                    if (string.IsNullOrEmpty(name)) continue;

                    var isRelMovie = IsMovieFormat((string)node.format);
                    var stremioType = isRelMovie ? MetaType.movie.ToString() : MetaType.series.ToString();
                    // Stremio web's hash-routed SPA uses #/detail/{type}/{id};
                    // anilist:{id}'s colon is URL-encoded so the route parser
                    // doesn't choke on the literal character.
                    var encodedId = Uri.EscapeDataString($"{anilistPrefix}{relId.Value}");
                    anime.links.Add(new Link
                    {
                        name = name,
                        category = label,
                        url = $"https://web.stremio.com/#/detail/{stremioType}/{encodedId}",
                        anilistId = relId.Value,
                    });
                    descriptionExtras.Add($"{label}: {name}");
                }

                if (descriptionExtras.Count > 0)
                {
                    var sep = string.IsNullOrEmpty(anime.description) ? string.Empty : "\n\n";
                    anime.description = (anime.description ?? string.Empty)
                        + sep + "Related:\n" + string.Join("\n", descriptionExtras);
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

            var data = await PostGraphQLAsync(requestBody, tokenData);
            if (data == null) return null;

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
                _logger.LogWarning("AniList save skipped — no AniList mapping for animeId={AnimeId} season={Season}.", animeId, season);
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

            await PostGraphQLMutationAsync(SerializeObject(new { query, variables }), tokenData, "save");
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

            await PostGraphQLMutationAsync(requestBody, tokenData, "delete");
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

            var data = await PostGraphQLAsync(requestBody);
            var media = data?.Media;
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

            // Public media query — same guard as GetAnimeByIdAsync so a
            // non-AniList primary's bearer doesn't get sent and 401 us.
            var data = await PostGraphQLAsync(requestBody, AnilistBearerOrAnonymous(tokenData));
            var media = data?.Media;
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

        public async Task<AnilistUserStats?> GetUserStatsAsync(TokenData tokenData)
        {
            if (string.IsNullOrEmpty(tokenData?.access_token) || string.IsNullOrEmpty(tokenData.user_id))
                return null;
            if (!int.TryParse(tokenData.user_id, out var userId)) return null;

            // No server-side cache: the dashboard hits /Home/AnilistStats
            // from JS at most once per 24 h per browser (localStorage TTL),
            // so a per-process IMemoryCache would mostly cache nothing
            // useful and double-up against the client cache. Per-page-load
            // round-trip moved off the SSR critical path too.
            //
            // Single GraphQL — no list pagination, no per-entry deserialisation.
            // statuses[] returns one row per MediaListStatus value the user has
            // any entries in (CURRENT, COMPLETED, PLANNING, …); we read the
            // counts for the two the dashboard surfaces.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($userId: Int) {
                        User(id: $userId) {
                            statistics {
                                anime {
                                    meanScore
                                    minutesWatched
                                    statuses { status count }
                                }
                            }
                        }
                    }",
                variables = new { userId },
            });

            var data = await PostGraphQLAsync(requestBody, tokenData);
            var anime = data?.User?.statistics?.anime;
            if (anime == null) return null;

            int watching = 0;
            int completed = 0;
            if (anime.statuses != null)
            {
                foreach (var s in anime.statuses)
                {
                    var status = (string)s.status;
                    var count = (int?)s.count ?? 0;
                    if (status == "CURRENT") watching = count;
                    else if (status == "COMPLETED") completed = count;
                }
            }

            // meanScore comes back 0-100 on AniList; normalise to 0-10 with one
            // decimal so it matches what the catalog Meta builders emit. Zero
            // means "user has no rated entries" — surface as null so the view's
            // "Mean" cell can hide instead of showing a misleading 0.0.
            var rawMean = (double?)anime.meanScore;
            double? meanScore = rawMean.HasValue && rawMean.Value > 0
                ? Math.Round(rawMean.Value / 10, 1)
                : (double?)null;

            var minutes = (int?)anime.minutesWatched ?? 0;
            var hours = minutes / 60;

            return new AnilistUserStats(watching, completed, hours, meanScore);
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

            var data = await PostGraphQLAsync(requestBody, tokenData);
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

