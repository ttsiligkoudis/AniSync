using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Public-mode counterpart to <see cref="LibraryController"/>: trending / seasonal /
    /// airing catalogs work without an authenticated list account, so /discover renders
    /// for everyone — anonymous visitors and logged-in users alike. Logged-in users see
    /// the data through their primary service so the Manage Entry hand-off works on the
    /// resulting cards; anonymous visitors fall back to Kitsu (matching the addon's
    /// anonymous default — see HomeController.Configure).
    /// </summary>
    public class DiscoverController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly IAnilistFallback _anilistFallback;
        private readonly IConfigStore _configStore;

        public DiscoverController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IAnilistFallback anilistFallback,
            IConfigStore configStore)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _anilistFallback = anilistFallback;
            _configStore = configStore;
        }

        // The three discover catalogs. Order is the tab strip rendering order;
        // Trending leads because it's the most-viewed catalog in the addon's
        // existing manifest analytics.
        private static readonly ListType[] DiscoverListTypes =
        [
            ListType.Trending_Desc,
            ListType.Seasonal,
            ListType.Airing,
        ];

        // Common anime genres surfaced in the picker. Hand-curated rather than
        // fetched from the AniList genre endpoint — gives consistent UI across
        // services (MAL/Kitsu use slightly different genre names) and avoids an
        // extra upstream call per dashboard render. Order is rough popularity.
        private static readonly string[] PopularGenres =
        [
            "Action", "Adventure", "Comedy", "Drama", "Slice of Life",
            "Romance", "Fantasy", "Sci-Fi", "Mystery", "Psychological",
            "Sports", "Supernatural", "Music", "Horror", "Thriller",
        ];

        [Route("/discover")]
        public async Task<IActionResult> Index(string list = null, string search = null, string genre = null, string season = null, string tag = null)
        {
            // Anonymous fresh-visit: GetAccessTokenAsync returns null. Synthesise an
            // anonymous TokenData with the Kitsu default so the per-service dispatch
            // below has something to switch on. The trending / seasonal / airing
            // catalogs don't need a user identity — the underlying GraphQL/REST calls
            // run unauthenticated. anonymousUser is a computed property (empty
            // username on Kitsu / empty access_token on the OAuth services) so
            // leaving the identity fields blank here makes it return true on its own.
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            var hasSearch = !string.IsNullOrWhiteSpace(search);
            var hasGenre = !string.IsNullOrWhiteSpace(genre);
            var hasTag = !string.IsNullOrWhiteSpace(tag);
            var activeList = ParseListType(list);

            // Resolve the row's UID for logged-in users so per-card Manage Entry links
            // hit the existing config-scoped flow. Anonymous users get null and the
            // view renders the cards as inert (no Manage Entry — they have no list).
            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            // Honor the "Hide unaired from Watching" pref. Only affects the
            // ListType.Current discover-only tab; harmless on every other list.
            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideUnreleased = configuration?.hideUnreleasedFromWatching == true;

            // Discover is always ungrouped — the enableSeasonGrouping pref now
            // only governs Stremio's catalog endpoints. The site renders every
            // cour as its own card so users can land directly on the season
            // they're interested in rather than navigating through a grouped
            // umbrella entry.
            var listForCall = hasSearch ? ListType.Search : activeList;
            const bool groupSeasonsForCall = false;
            // Tag is an AniList-native filter — MAL/Kitsu don't expose an
            // equivalent — so when a tag is requested we route through
            // AnilistFallback's anonymous browse regardless of the viewer's
            // primary service. Cards still translate ids into the user's
            // primary's space so Manage Entry / detail-page hand-offs work.
            List<Meta> metas;
            if (hasTag)
            {
                metas = await _anilistFallback.GetByTagAsync(tag, tokenData.anime_service);
            }
            else
            {
                metas = tokenData.anime_service switch
                {
                    AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased),
                    _                        => await _kitsuService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased),
                };
            }

            // Same 0.4-threshold relevance re-rank CatalogController applies on the
            // Stremio side — keeps web-app search behaviour consistent with the addon.
            if (hasSearch && metas?.Count > 0)
            {
                var normalisedQuery = NormalizeTitle(search);
                const double minScore = 0.4;
                metas = metas
                    .Select(m => (meta: m, score: ScoreMatch(normalisedQuery, m.name)))
                    .Where(x => x.score >= minScore)
                    .OrderByDescending(x => x.score)
                    .Select(x => x.meta)
                    .ToList();
            }

            // Season dropdown drives only the Seasonal list type. Default is
            // the current season label so the picker reads "Spring 2026"
            // out of the box rather than blank. The picker options span the
            // last 10 years + the upcoming season.
            var resolvedSeason = !string.IsNullOrWhiteSpace(season)
                ? season.Trim()
                : CurrentSeasonLabel();

            return View(new DiscoverViewModel
            {
                ConfigUid = uid,
                AnimeService = tokenData.anime_service,
                AnonymousUser = tokenData.anonymousUser,
                ActiveList = activeList,
                Tabs = DiscoverListTypes,
                Search = hasSearch ? search.Trim() : null,
                Genre = hasGenre ? genre.Trim() : null,
                AvailableGenres = PopularGenres,
                Season = resolvedSeason,
                AvailableSeasons = BuildSeasonalDropdownOptions(),
                Tag = hasTag ? tag.Trim() : null,
                Items = metas ?? [],
            });
        }

        /// <summary>
        /// Partial-HTML pagination endpoint driving the infinite-scroll on
        /// /discover. Same auth + service resolution as <see cref="Index"/>
        /// — but returns only the next chunk of poster cards (via the
        /// shared _PosterGrid partial) so the client can append them to
        /// the existing grid without rerendering the chrome around it.
        ///
        /// Search mode is excluded here on purpose — search results are
        /// already a single-shot relevance-ranked slice, not a paginated
        /// list, so the scroll handler simply doesn't fire on search pages.
        /// </summary>
        [Route("/discover/page")]
        public async Task<IActionResult> Page(string list, string genre = null, string skip = null, string season = null, string search = null, string tag = null, bool fullPane = false)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            var hasSearch = !string.IsNullOrWhiteSpace(search);
            var activeList = ParseListType(list);

            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideUnreleased = configuration?.hideUnreleasedFromWatching == true;

            // Search runs as ListType.Search across the full anime database;
            // matches the addon-side branch. Pagination scroll calls don't
            // pass a search term so this stays the existing per-tab dispatch.
            var listForCall = hasSearch ? ListType.Search : activeList;
            const bool groupSeasonsForCall = false;
            var hasTag = !string.IsNullOrWhiteSpace(tag);
            List<Meta> metas;
            if (hasTag)
            {
                // Mirror Index's tag-routing: tag filtering goes through the
                // AniList anonymous browse regardless of primary.
                metas = await _anilistFallback.GetByTagAsync(tag, tokenData.anime_service, skip);
            }
            else
            {
                metas = tokenData.anime_service switch
                {
                    AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listForCall, skip: skip, genre: genre, search: search, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listForCall, skip: skip, genre: genre, search: search, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased),
                    _                        => await _kitsuService.GetAnimeListAsync(tokenData, listForCall, skip: skip, genre: genre, search: search, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased),
                };
            }

            if (hasSearch && metas?.Count > 0)
            {
                var normalisedQuery = NormalizeTitle(search);
                const double minScore = 0.4;
                metas = metas
                    .Select(m => (meta: m, score: ScoreMatch(normalisedQuery, m.name)))
                    .Where(x => x.score >= minScore)
                    .OrderByDescending(x => x.score)
                    .Select(x => x.meta)
                    .ToList();
            }

            var labels = new Dictionary<ListType, string>
            {
                [ListType.Trending_Desc] = "Trending",
                [ListType.Seasonal]      = "Seasonal",
                [ListType.Airing]        = "Airing",
            };
            var activeLabel = labels.TryGetValue(activeList, out var l) ? l : "anime";
            var hasGenre = !string.IsNullOrWhiteSpace(genre);

            var gridModel = new PosterGridViewModel
            {
                Items = metas ?? [],
                ConfigUid = uid,
                // Empty-message stays null for infinite-scroll appends (no
                // skip = initial call; with skip = pagination chunk where an
                // empty page just stops the observer). fullPane callers
                // (filter-search.js Search-button swap) want the same empty
                // copy the server-rendered Index path uses.
                EmptyMessage = fullPane
                    ? (hasSearch
                        ? $"No matches for \"{search.Trim()}\"."
                        : hasGenre
                            ? $"No {genre.Trim().ToLower()} anime in {activeLabel.ToLower()} right now."
                            : $"Couldn't load {activeLabel.ToLower()} anime — try again later.")
                    : null,
            };

            return PartialView("_PosterGrid", gridModel);
        }

        private static ListType ParseListType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return ListType.Trending_Desc;
            if (Enum.TryParse<ListType>(raw, ignoreCase: true, out var parsed)
                && Array.IndexOf(DiscoverListTypes, parsed) >= 0)
                return parsed;
            // Allow ?list=trending as the URL-friendly synonym for Trending_Desc — the
            // underscore in the enum name is a code-side artifact users shouldn't have
            // to know about.
            if (string.Equals(raw, "trending", StringComparison.OrdinalIgnoreCase))
                return ListType.Trending_Desc;
            return ListType.Trending_Desc;
        }

        // ─── Browse-by-studio ───────────────────────────────────────────
        // Listing + per-studio detail live under /discover/studio so the
        // URL surface tracks the Browse By card on the home page.

        [Route("/discover/studio")]
        public async Task<IActionResult> Studios()
        {
            var (studios, _) = await _anilistFallback.GetStudiosListAsync(page: 1);
            return View("Studios", studios);
        }

        [Route("/discover/studio/page")]
        public async Task<IActionResult> StudiosPage(int page = 1)
        {
            var (studios, hasNext) = await _anilistFallback.GetStudiosListAsync(page);
            Response.Headers["X-Has-Next-Page"] = hasNext ? "true" : "false";
            return PartialView("_StudioTiles", studios ?? new List<StudioSummary>());
        }

        [Route("/discover/studio/{id:int}")]
        [Route("/discover/studio/{id:int}/{slug?}")]
        public async Task<IActionResult> Studio(int id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var (name, items, _) = await _anilistFallback.GetStudioMediaAsync(id, tokenData.anime_service);
            return View("StudioDetail", new StudioDetailViewModel
            {
                Id = id,
                Name = name,
                ConfigUid = uid,
                Items = items ?? [],
            });
        }

        [Route("/discover/studio/{id:int}/page")]
        public async Task<IActionResult> StudioPage(int id, int page = 1)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var (_, items, hasNext) = await _anilistFallback.GetStudioMediaAsync(id, tokenData.anime_service, page);
            Response.Headers["X-Has-Next-Page"] = hasNext ? "true" : "false";
            return PartialView("_PosterGrid", new PosterGridViewModel
            {
                Items = items ?? new List<Meta>(),
                ConfigUid = uid,
            });
        }

        // ─── Staff drill-down ───────────────────────────────────────────
        // Single anime-by-staff filmography page reached from a staff
        // chip on the anime detail page. Not a "browse" affordance (no
        // listing of all staff — AniList has tens of thousands), just
        // the per-id catalog.

        [Route("/discover/staff/{id:int}")]
        [Route("/discover/staff/{id:int}/{slug?}")]
        public async Task<IActionResult> Staff(int id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var (name, items) = await _anilistFallback.GetStaffMediaAsync(id, tokenData.anime_service);

            return View("StaffDetail", new StaffDetailViewModel
            {
                Id = id,
                Name = name,
                ConfigUid = uid,
                Items = items ?? [],
            });
        }

        // ─── Browse-by-tag ──────────────────────────────────────────────
        // Listing: every AniList tag in one render (MediaTagCollection is
        // unpaginated upstream — ~300 entries). Detail: poster grid for a
        // single tag with infinite scroll. /discover/tag/{tagStr} is the
        // dedicated landing URL; the /discover?tag=X query-param path
        // remains for filter-bar interactions inside the main /discover
        // page.

        [Route("/discover/tag")]
        public async Task<IActionResult> Tags()
        {
            var tags = await _anilistFallback.GetTagsListAsync();
            return View("Tags", tags ?? new List<TagSummary>());
        }

        [Route("/discover/tag/{tagStr}")]
        public async Task<IActionResult> Tag(string tagStr)
        {
            if (string.IsNullOrWhiteSpace(tagStr))
            {
                return RedirectToAction(nameof(Tags));
            }

            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var (items, _) = await _anilistFallback.GetByTagPageAsync(tagStr, tokenData.anime_service, page: 1);
            return View("TagDetail", new TagDetailViewModel
            {
                Tag = tagStr,
                ConfigUid = uid,
                Items = items ?? [],
            });
        }

        [Route("/discover/tag/{tagStr}/page")]
        public async Task<IActionResult> TagPage(string tagStr, int page = 1)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var (items, hasNext) = await _anilistFallback.GetByTagPageAsync(tagStr, tokenData.anime_service, page);
            Response.Headers["X-Has-Next-Page"] = hasNext ? "true" : "false";
            return PartialView("_PosterGrid", new PosterGridViewModel
            {
                Items = items ?? new List<Meta>(),
                ConfigUid = uid,
            });
        }
    }

    public class StudioDetailViewModel
    {
        public int Id { get; set; }
        // Null when the studio id didn't resolve — view renders "Unknown".
        public string Name { get; set; }
        public string ConfigUid { get; set; }
        public List<Meta> Items { get; set; } = [];
    }

    public class StaffDetailViewModel
    {
        public int Id { get; set; }
        // Null when the staff id didn't resolve — view renders "Unknown".
        public string Name { get; set; }
        public string ConfigUid { get; set; }
        public List<Meta> Items { get; set; } = [];
    }

    public class TagDetailViewModel
    {
        public string Tag { get; set; }
        public string ConfigUid { get; set; }
        public List<Meta> Items { get; set; } = [];
    }

    /// <summary>
    /// View model for the discover page (Views/Discover/Index.cshtml). Mirrors
    /// LibraryViewModel's shape so the shared partial + tab strip work identically.
    /// </summary>
    public class DiscoverViewModel
    {
        public string ConfigUid { get; set; }
        public AnimeService AnimeService { get; set; }
        public bool AnonymousUser { get; set; }
        public ListType ActiveList { get; set; }
        public IReadOnlyList<ListType> Tabs { get; set; } = [];
        // The active search term, or null when the page is in tab-list mode. The
        // view renders a search-results header (and hides the tabs) when this is set.
        public string Search { get; set; }
        // Active genre filter (e.g. "Action"), or null when all genres show. The
        // view renders an "× clear" pill alongside the genre dropdown when set.
        public string Genre { get; set; }
        public IReadOnlyList<string> AvailableGenres { get; set; } = [];
        // AniList-native tag filter — engaged when the user followed a Tag
        // chip on a detail page. Routes through AnilistFallback's anonymous
        // browse regardless of the user's primary, since MAL/Kitsu don't
        // expose an equivalent.
        public string Tag { get; set; }
        // Active season label ("Spring 2026") — only meaningful when the active
        // list type is Seasonal. Always populated (current season is the fall-
        // back) so the dropdown can preselect the correct option.
        public string Season { get; set; }
        public IReadOnlyList<string> AvailableSeasons { get; set; } = [];
        public List<Meta> Items { get; set; } = [];
    }
}
