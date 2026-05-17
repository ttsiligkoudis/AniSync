using AnimeList.Models;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Web-app surface for browsing the user's library inside AniSync (rather than via
    /// Stremio). Session-based auth — the user logs in via /Auth/* on the configure page,
    /// the cookie is set, and the library routes here use it instead of the path-config
    /// pattern that the Stremio addon endpoints (CatalogController, MetaController)
    /// require. Calls into the same per-service GetAnimeListAsync the addon does, so the
    /// data path is identical; only the auth model and the rendering differ.
    /// </summary>
    public class LibraryController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly IConfigStore _configStore;

        public LibraryController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IConfigStore configStore)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
        }

        // The six list types the user-list catalogs map to. Trending/Seasonal/Airing
        // are the discover catalogs (handled by DiscoverController); Search isn't a
        // tab. Order here is the tab strip rendering order.
        private static readonly ListType[] UserListTypes =
        [
            ListType.Current,
            ListType.Completed,
            ListType.Planning,
            ListType.Paused,
            ListType.Dropped,
            ListType.Repeating,
        ];

        // Mirrors DiscoverController.PopularGenres — hand-curated common
        // anime genres surfaced in the picker. Duplicated rather than shared
        // out so the two controllers stay independently editable; the list
        // is small enough that drift is unlikely to bite.
        private static readonly string[] PopularGenres =
        [
            "Action", "Adventure", "Comedy", "Drama", "Slice of Life",
            "Romance", "Fantasy", "Sci-Fi", "Mystery", "Psychological",
            "Sports", "Supernatural", "Music", "Horror", "Thriller",
        ];

        [Route("/library")]
        public async Task<IActionResult> Index(string list = null, string search = null, string genre = null)
        {
            // Session-based identity. /library has no path-config — it's pure web app.
            // Anonymous visitors and not-logged-in sessions get bounced to the dashboard
            // because user lists require a real list account; the dashboard already
            // explains that and offers a Configure-page CTA where they can log in.
            // ResolveCurrentAsync returns uid == null for both unauthenticated and
            // anonymous sessions, so a single null check covers both cases.
            var (tokenData, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            if (uid == null) return RedirectToAction("Index", "Home");

            var hasSearch = !string.IsNullOrWhiteSpace(search);
            var hasGenre = !string.IsNullOrWhiteSpace(genre);
            var activeList = ParseListType(list);

            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideUnreleased = configuration?.hideUnreleasedFromWatching == true;

            // Library scopes search to the active tab's user list — typing
            // "naruto" on Watching surfaces only Naruto entries already in
            // Watching, not every Naruto in the catalog. So the upstream
            // dispatch always uses activeList (Watching/Completed/etc.); the
            // search term is applied as an in-memory post-filter below.
            var (metas, useUpstreamPaging) = await FetchAsync(tokenData, activeList, search, genre, hasSearch, hasGenre, hideUnreleased, requestedSkip: 0);

            return View(new LibraryViewModel
            {
                ConfigUid = uid,
                AnimeService = tokenData.anime_service,
                ActiveList = activeList,
                Tabs = UserListTypes,
                Search = hasSearch ? search.Trim() : null,
                Genre = hasGenre ? genre.Trim() : null,
                AvailableGenres = PopularGenres,
                Items = metas,
                // Scroll pagination only kicks in for upstream-paginated mode
                // (MAL/Kitsu, no filter). AniList returns the whole library in
                // one MediaListCollection call so we render every card up front
                // and the scroll listener doesn't even bind. Filter modes also
                // render flat — search and genre are both post-fetch for user
                // lists, so a single full-library fetch is the only way to
                // surface every match in one shot.
                Paginated = useUpstreamPaging && metas.Count > 0,
            });
        }

        private static ListType ParseListType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return ListType.Current;
            if (Enum.TryParse<ListType>(raw, ignoreCase: true, out var parsed)
                && Array.IndexOf(UserListTypes, parsed) >= 0)
                return parsed;
            return ListType.Current;
        }

        /// <summary>
        /// Returns just the poster-grid HTML (or the full pane partial when
        /// fullPane=true) for the requested filter combo + scroll offset, so
        /// library-pagination.js can append on scroll and filter-search.js can
        /// swap the results pane in place. Mirrors Index's fetch path —
        /// upstream-paginated for MAL/Kitsu with no filter, full-library for
        /// AniList or any filter mode. Anonymous users are bounced (the JS
        /// shouldn't hit this for them, but defend in depth).
        /// </summary>
        [Route("/library/page")]
        public async Task<IActionResult> Page(string list = null, string search = null, string genre = null, int skip = 0, bool fullPane = false)
        {
            var (tokenData, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            if (uid == null) return Unauthorized();

            var hasSearch = !string.IsNullOrWhiteSpace(search);
            var hasGenre = !string.IsNullOrWhiteSpace(genre);
            var activeList = ParseListType(list);
            var labels = new Dictionary<ListType, string>
            {
                [ListType.Current]   = "Watching",
                [ListType.Completed] = "Completed",
                [ListType.Planning]  = "Planning",
                [ListType.Paused]    = "Paused",
                [ListType.Dropped]   = "Dropped",
                [ListType.Repeating] = "Rewatching",
            };

            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideUnreleased = configuration?.hideUnreleasedFromWatching == true;

            // fullPane swap (filter-search Submit) resets to the first upstream
            // page; the paginator data-skip in the swapped HTML starts the next
            // scroll run from there. Scroll appends pass skip explicitly. Filter
            // mode and AniList ignore skip — FetchAsync fetches the whole
            // library either way.
            var requestedSkip = fullPane ? 0 : Math.Max(0, skip);
            var (metas, useUpstreamPaging) = await FetchAsync(tokenData, activeList, search, genre, hasSearch, hasGenre, hideUnreleased, requestedSkip);

            var activeLabel = labels[activeList];
            var grid = new PosterGridViewModel
            {
                Items = metas,
                ConfigUid = uid,
                EmptyMessage = fullPane
                    ? (hasSearch
                        ? $"No matches for \"{search.Trim()}\"."
                        : hasGenre
                            ? $"No {genre.Trim().ToLower()} anime in your {activeLabel} list."
                            : $"Nothing in your {activeLabel} list on {tokenData.anime_service}.")
                    : null,
            };

            // fullPane swaps come from the Search-button click and want the
            // shared pane partial (paginator wrapper + sentinel for upstream-
            // paged views, plain grid for AniList / filter modes) so
            // library-pagination.js can rebind against the freshly-rendered
            // wrapper. Scroll appends just want the grid chunk to splice into
            // the existing one.
            if (fullPane)
            {
                return PartialView("_LibraryPane", new LibraryPaneViewModel
                {
                    Grid = grid,
                    ShowPaginator = useUpstreamPaging && metas.Count > 0,
                    ListSlug = list?.ToLower() ?? activeList.ToString().ToLower(),
                    Search = hasSearch ? search.Trim() : null,
                    Genre = hasGenre ? genre.Trim() : null,
                    // data-skip seeds the next scroll request; on the first
                    // upstream page this is the page size, which the JS adds
                    // to as more cards land.
                    Skip = metas.Count,
                });
            }
            return PartialView("_PosterGrid", grid);
        }

        /// <summary>
        /// Shared fetch + in-memory transform pipeline for Index and Page.
        /// Decides per-service whether to paginate upstream or fetch the whole
        /// library, dispatches the call, then applies search ranking (when
        /// active) and alphabetical sorting (only when we have the entire
        /// library in memory — per-page sort would jumble order across
        /// boundaries). The <paramref name="requestedSkip"/> argument is the
        /// scroll offset the JS is asking to start at — only consulted in
        /// upstream-paging mode (MAL/Kitsu with no filter). Other modes always
        /// fetch the whole library; the JS doesn't scroll-fetch on those.
        /// </summary>
        private async Task<(List<Meta> metas, bool useUpstreamPaging)> FetchAsync(
            TokenData tokenData, ListType activeList, string search, string genre,
            bool hasSearch, bool hasGenre, bool hideUnreleased, int requestedSkip)
        {
            // No caching on the library path — every load reflects live state
            // off the upstream service. Mirrors DiscoverController: MAL/Kitsu
            // paginate upstream one page at a time, AniList returns the whole
            // library in one MediaListCollection call (no nested pagination).
            // Filter modes (search or genre) fall back to a full-library fetch
            // because both filters are post-fetch for user-list endpoints, so
            // per-page filtering would return sparse or empty pages.
            var isAnilist = tokenData.anime_service == AnimeService.Anilist;
            var useUpstreamPaging = !isAnilist && !hasSearch && !hasGenre;
            // Service-level skip-presence is the trigger for "single upstream
            // page" vs "fetch the whole library" — see MalService /
            // KitsuService. Pass null when we want everything.
            var serviceSkip = useUpstreamPaging ? requestedSkip.ToString() : null;
            const bool groupSeasonsForCall = false;

            var metas = tokenData.anime_service switch
            {
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, activeList, serviceSkip, genre: genre, groupSeasons: groupSeasonsForCall, hideUnreleased: hideUnreleased),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, activeList, serviceSkip, genre: genre, groupSeasons: groupSeasonsForCall, hideUnreleased: hideUnreleased),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, activeList, serviceSkip, genre: genre, groupSeasons: groupSeasonsForCall, hideUnreleased: hideUnreleased),
            };

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

            metas ??= [];

            // Alphabetical sort only when we have the entire library in memory.
            // In upstream-paged mode we'd be sorting one page at a time, which
            // would shuffle the global order across page boundaries — let
            // MAL's list_updated_at / Kitsu's progressed_at ordering carry
            // through instead. Search already imposes its ScoreMatch ranking.
            if (!hasSearch && !useUpstreamPaging)
            {
                metas = metas
                    .OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return (metas, useUpstreamPaging);
        }
    }

    /// <summary>
    /// View model for the library page (Views/Library/Index.cshtml). Carries the
    /// resolved UID so per-card Manage Entry links can hit the existing config-scoped
    /// route, plus the user's primary service and the active list type for the tabs.
    /// </summary>
    public class LibraryViewModel
    {
        public string ConfigUid { get; set; }
        public AnimeService AnimeService { get; set; }
        public ListType ActiveList { get; set; }
        public IReadOnlyList<ListType> Tabs { get; set; } = [];
        // The active search term, or null when the page is in tab-list mode. The
        // view renders a search-results header (and hides the tabs) when this is set.
        public string Search { get; set; }
        // Active genre filter (e.g. "Action"), or null when all genres show. Same
        // shape as DiscoverViewModel — the picker is rendered with the same form
        // pattern, preserved across list-tab navigation and search submissions.
        public string Genre { get; set; }
        public IReadOnlyList<string> AvailableGenres { get; set; } = [];
        public List<Meta> Items { get; set; } = [];
        // True when scroll pagination should fire — only in upstream-paged
        // mode (MAL/Kitsu, no filter). AniList user lists and filter modes
        // (search or genre) render every match server-side in one go, so the
        // scroll listener stays unbound.
        public bool Paginated { get; set; }
    }
}
