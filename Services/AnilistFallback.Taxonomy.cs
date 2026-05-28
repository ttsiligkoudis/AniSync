using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace AnimeList.Services
{
    /// <summary>
    /// <see cref="AnilistFallback"/> partial: taxonomy browse — tags, staff, studios.
    /// Backs /discover/tag/{name}, /discover/staff/{id}, /discover/studio/{id}, and the
    /// "Tags" + "Studios" index pages. Each call returns Stremio-shaped Meta cards
    /// translated to the caller's primary id space.
    /// </summary>
    public partial class AnilistFallback
    {
        public async Task<List<Meta>> GetByTagAsync(string tag, AnimeService translateTo, string skip = null, bool hideAdult = false, bool groupSeasons = false)
        {
            if (string.IsNullOrWhiteSpace(tag)) return [];
            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;
            // Page.media() supports isAdult — filter server-side so we
            // don't waste a page slot on entries we'd drop anyway.
            var adultArg = hideAdult ? ", isAdult: false" : string.Empty;

            // POPULARITY_DESC so the user lands on the heaviest hitters
            // for the tag first — Re:ZERO before some obscure 2003 OVA
            // sharing the same theme. Same convention the catalog's
            // "Trending" / "Popular this season" shelves use.
            var requestBody = SerializeObject(new
            {
                query = $@"
                    query ($page: Int, $perPage: Int, $tag: String) {{
                        Page(page: $page, perPage: $perPage) {{
                            media(type: ANIME, tag: $tag, sort: POPULARITY_DESC{adultArg}) {{
                                id
                                isAdult
                                format
                                episodes
                                averageScore
                                seasonYear
                                title {{ english romaji }}
                                coverImage {{ large }}
                                description
                            }}
                        }}
                    }}",
                variables = new { page, perPage = PageSize, tag },
            });

            var data = await PostJsonAsync(requestBody);
            return await BuildBrowseMetasAsync(data?.Page?.media, translateTo, hideAdult, groupSeasons);
        }

        public async Task<(List<Meta> Items, bool HasNextPage)> GetByTagPageAsync(string tag, AnimeService translateTo, int page = 1, bool hideAdult = false, bool groupSeasons = false)
        {
            if (string.IsNullOrWhiteSpace(tag)) return ([], false);
            if (page < 1) page = 1;
            var adultArg = hideAdult ? ", isAdult: false" : string.Empty;

            var requestBody = SerializeObject(new
            {
                query = $@"
                    query ($page: Int, $perPage: Int, $tag: String) {{
                        Page(page: $page, perPage: $perPage) {{
                            pageInfo {{ hasNextPage }}
                            media(type: ANIME, tag: $tag, sort: POPULARITY_DESC{adultArg}) {{
                                id
                                isAdult
                                format
                                episodes
                                averageScore
                                seasonYear
                                title {{ english romaji }}
                                coverImage {{ large }}
                                description
                            }}
                        }}
                    }}",
                variables = new { page, perPage = PageSize, tag },
            });

            var data = await PostJsonAsync(requestBody);
            var items = await BuildBrowseMetasAsync(data?.Page?.media, translateTo, hideAdult, groupSeasons);
            bool hasNext = data?.Page?.pageInfo?.hasNextPage != null
                && (bool)data.Page.pageInfo.hasNextPage;
            return (items, hasNext);
        }

        public async Task<List<TagSummary>> GetTagsListAsync()
        {
            const string cacheKey = "anilist:tags-list:by-category";
            if (_cache.TryGetValue<List<TagSummary>>(cacheKey, out var cached) && cached != null)
            {
                return cached;
            }

            // MediaTagCollection isn't paginated upstream — AniList returns
            // every tag in one shot (a few hundred entries). Adult-only
            // tags are dropped here rather than at render time so the
            // server-side cache stays consistent with what we ever surface.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query {
                        MediaTagCollection {
                            name
                            category
                            description
                            isAdult
                        }
                    }",
            });

            var data = await PostJsonAsync(requestBody);
            var tags = new List<TagSummary>();
            if (data?.MediaTagCollection != null)
            {
                foreach (var t in data.MediaTagCollection)
                {
                    if (t == null) continue;
                    var name = (string)t.name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (t.isAdult != null && (bool)t.isAdult) continue;
                    tags.Add(new TagSummary
                    {
                        Name = name,
                        Category = (string)t.category,
                        Description = (string)t.description,
                    });
                }
            }

            // Sort by category, then name — lets the view render section
            // headers (Theme – Action, Theme – Romance, Setting-Time, …)
            // without an extra group-by pass. Tags with no category land
            // under an "Other" bucket at the top alphabetically.
            tags.Sort((a, b) =>
            {
                var ca = a.Category ?? string.Empty;
                var cb = b.Category ?? string.Empty;
                var byCat = string.Compare(ca, cb, StringComparison.OrdinalIgnoreCase);
                return byCat != 0 ? byCat : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            if (tags.Count > 0)
            {
                _cache.Set(cacheKey, tags, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24),
                });
            }
            return tags;
        }

        public async Task<(string Name, List<Meta> Items)> GetStaffMediaAsync(int staffId, AnimeService translateTo, string skip = null, bool hideAdult = false, bool groupSeasons = false)
        {
            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;

            // Staff.staffMedia doesn't accept an isAdult filter arg on the
            // GraphQL connection, so we select the field and filter inside
            // BuildBrowseMetasAsync. Costs one boolean per node — cheap.
            // POPULARITY_DESC so the staff member's most-watched credits
            // surface first — what users come to a staff page for.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int, $page: Int, $perPage: Int) {
                        Staff(id: $id) {
                            name { full }
                            staffMedia(type: ANIME, sort: POPULARITY_DESC, page: $page, perPage: $perPage) {
                                edges {
                                    node {
                                        id
                                        isAdult
                                        format
                                        episodes
                                        averageScore
                                        seasonYear
                                        title { english romaji }
                                        coverImage { large }
                                        description
                                    }
                                }
                            }
                        }
                    }",
                variables = new { id = staffId, page, perPage = PageSize },
            });

            var data = await PostJsonAsync(requestBody);
            var staff = data?.Staff;
            if (staff == null) return (null, []);

            var name = (string)staff.name?.full;
            // Flatten edges → node array so BuildBrowseMetasAsync can reuse
            // the same shape it uses for the tag / studio paths.
            var nodes = new List<dynamic>();
            if (staff.staffMedia?.edges != null)
            {
                foreach (var edge in staff.staffMedia.edges)
                    if (edge.node != null) nodes.Add(edge.node);
            }
            return (name, await BuildBrowseMetasAsync(nodes, translateTo, hideAdult, groupSeasons));
        }

        public async Task<(string Name, List<Meta> Items, bool HasNextPage)> GetStudioMediaAsync(int studioId, AnimeService translateTo, int page = 1, bool hideAdult = false, bool groupSeasons = false)
        {
            if (page < 1) page = 1;

            // Studio.media doesn't accept an isAdult filter arg on the
            // GraphQL connection — same as Staff.staffMedia. Select the
            // field and filter inside BuildBrowseMetasAsync.
            // POPULARITY_DESC so the studio's flagship shows lead — what
            // users come to a studio page to see.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int, $page: Int, $perPage: Int) {
                        Studio(id: $id) {
                            name
                            media(sort: POPULARITY_DESC, page: $page, perPage: $perPage) {
                                pageInfo { hasNextPage }
                                edges {
                                    node {
                                        id
                                        isAdult
                                        type
                                        format
                                        episodes
                                        averageScore
                                        seasonYear
                                        title { english romaji }
                                        coverImage { large }
                                        description
                                    }
                                }
                            }
                        }
                    }",
                variables = new { id = studioId, page, perPage = PageSize },
            });

            var data = await PostJsonAsync(requestBody);
            var studio = data?.Studio;
            if (studio == null) return (null, [], false);

            var name = (string)studio.name;
            var nodes = new List<dynamic>();
            if (studio.media?.edges != null)
            {
                foreach (var edge in studio.media.edges)
                {
                    if (edge.node == null) continue;
                    // Studio.media returns Manga + Anime mixed. Filter to
                    // anime-only on the client since the GraphQL Studio.media
                    // arg list doesn't expose a type filter directly.
                    if ((string)edge.node.type != "ANIME") continue;
                    nodes.Add(edge.node);
                }
            }

            // hasNextPage from AniList itself — independent of how many
            // anime survived the manga filter above. A page can render
            // zero cards (all manga) while more anime pages still
            // follow, so the paginator can't infer end-of-list from
            // list size alone.
            bool hasNext = studio.media?.pageInfo?.hasNextPage != null
                && (bool)studio.media.pageInfo.hasNextPage;

            return (name, await BuildBrowseMetasAsync(nodes, translateTo, hideAdult, groupSeasons), hasNext);
        }

        public async Task<(List<StudioSummary> Studios, bool HasNextPage)> GetStudiosListAsync(int page = 1, string search = null)
        {
            if (page < 1) page = 1;
            var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            var hasSearch = trimmedSearch != null;

            // Cache by (search, page) so the favourites default doesn't poison
            // search results and vice versa. Search hits are rare per term, but
            // the user paging through a single search benefits from caching.
            var cacheKey = hasSearch
                ? $"anilist:studios-list:search:{trimmedSearch.ToLowerInvariant()}:p{page}"
                : $"anilist:studios-list:by-favourites:p{page}";
            if (_cache.TryGetValue<(List<StudioSummary>, bool)>(cacheKey, out var cached) && cached.Item1 != null)
            {
                return cached;
            }

            // perPage=50 matches the discover paginator's chunk size so the
            // user feels the same scroll-loaded cadence between anime and
            // studio listings. FAVOURITES_DESC surfaces the studios the
            // user is most likely to recognise first (Mappa, Madhouse,
            // Ghibli, …) and tapers down into long-tail entries as they
            // scroll. isAnimationStudio + the media pageInfo.total filter
            // out manga / LN labels and empty entries so every rendered
            // tile points at a catalog with at least one anime. NB:
            // Studio.media does NOT accept a `type` argument — passing
            // one 500s the whole query — so the anime/non-anime cut
            // happens via isAnimationStudio.
            //
            // Search branch: AniList's studios() page takes an optional
            // `search` arg that does fuzzy name matching server-side.
            // Sort is dropped in this branch — search results carry
            // their own relevance ranking and re-sorting by favourites
            // would dilute it. The $search variable + its declaration
            // only appear when we're actually searching: AniList's
            // GraphQL rejects unused variable declarations, so the
            // default-sort path stays free of it.
            string requestBody;
            if (hasSearch)
            {
                requestBody = SerializeObject(new
                {
                    query = @"
                        query ($page: Int, $perPage: Int, $search: String) {
                            Page(page: $page, perPage: $perPage) {
                                pageInfo { hasNextPage }
                                studios(search: $search) {
                                    id
                                    name
                                    isAnimationStudio
                                    media { pageInfo { total } }
                                }
                            }
                        }",
                    variables = new { page, perPage = 50, search = trimmedSearch },
                });
            }
            else
            {
                requestBody = SerializeObject(new
                {
                    query = @"
                        query ($page: Int, $perPage: Int) {
                            Page(page: $page, perPage: $perPage) {
                                pageInfo { hasNextPage }
                                studios(sort: FAVOURITES_DESC) {
                                    id
                                    name
                                    isAnimationStudio
                                    media { pageInfo { total } }
                                }
                            }
                        }",
                    variables = new { page, perPage = 50 },
                });
            }

            var data = await PostJsonAsync(requestBody);
            if (data?.Page?.studios == null)
            {
                // Upstream errored / rate-limited — don't cache, let the
                // next request retry. Empty list + HasNextPage=false
                // stops the client paginator gracefully without 500ing.
                return (new List<StudioSummary>(), false);
            }

            var studios = new List<StudioSummary>();
            foreach (var s in data.Page.studios)
            {
                if (s == null) continue;
                var name = (string)s.name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                bool isAnimation = s.isAnimationStudio != null && (bool)s.isAnimationStudio;
                if (!isAnimation) continue;
                int count = 0;
                if (s.media?.pageInfo?.total != null)
                {
                    count = (int)s.media.pageInfo.total;
                }
                if (count <= 0) continue;
                studios.Add(new StudioSummary
                {
                    Id = (int)s.id,
                    Name = name,
                    AnimeCount = count,
                });
            }

            // hasNextPage comes from AniList's own pageInfo — independent
            // of how many studios survived our client-side filter. A page
            // can render zero tiles (all 50 entries were manga labels)
            // and still have more real pages after; the caller must use
            // this flag, not list size, to decide when to stop scrolling.
            bool hasNext = data.Page.pageInfo?.hasNextPage != null && (bool)data.Page.pageInfo.hasNextPage;

            var result = (studios, hasNext);
            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24),
            });
            return result;
        }
    }
}
