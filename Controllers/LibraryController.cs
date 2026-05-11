using AnimeList.Models;
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

        // Page size for the infinite-scroll slice. Mirrors Discover's
        // CatalogPageSize so the cards-per-row layout is consistent and the
        // sentinel triggers at the same scroll depth.
        private const int LibraryPageSize = 50;

        [Route("/library")]
        public async Task<IActionResult> Index(string list = null, string search = null, string genre = null)
        {
            // Session-based identity. /library has no path-config — it's pure web app.
            // Anonymous visitors and not-logged-in sessions get bounced to the dashboard
            // because user lists require a real list account; the dashboard already
            // explains that and offers a Configure-page CTA where they can log in.
            var tokenData = await _tokenService.GetAccessTokenAsync();
            if (tokenData == null || tokenData.anonymousUser)
                return RedirectToAction("Index", "Home");

            var hasSearch = !string.IsNullOrWhiteSpace(search);
            var hasGenre = !string.IsNullOrWhiteSpace(genre);

            // Parse the ?list= query param; default to Current and silently coerce
            // unknown / non-user-list values rather than 400'ing — keeps shareable URLs
            // tolerant of typos and old links.
            var activeList = ParseListType(list);

            // Resolve the row's UID so the Manage Entry links on each card can use the
            // existing config-scoped /{config}/Meta/ManageEntry/{id} flow without us
            // having to surface the UID in the visible URL — it stays an implementation
            // detail of the link href.
            var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);

            // Library is always ungrouped — the enableSeasonGrouping pref now
            // only governs Stremio's catalog / meta endpoints. The site
            // surfaces every cour as its own row so "what's in my Watching
            // list" reads honestly; the addon-only toggle is for the Stremio
            // discovery shelf where collapsing cours to one card is the
            // expected Stremio-side behaviour.
            //
            // Search ran with groupSeasons=false even under the old "honour
            // the toggle" code (collapsing seasons rewrites titles to the
            // shortest variant which fights the "find this specific anime"
            // intent), so the dispatch is now uniformly ungrouped regardless
            // of search mode.
            var listForCall = hasSearch ? ListType.Search : activeList;
            const bool groupSeasonsForCall = false;

            // No caching on the library path — each load reflects live state
            // off the upstream service. Each per-service call internally
            // returns the full list (services paginate upstream where
            // possible); we slice the visible window here to LibraryPageSize
            // so the first render is fast and the rest streams in through
            // library-pagination.js.
            var metas = tokenData.anime_service switch
            {
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall),
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
            var total = metas.Count;
            // Search is single-shot (already relevance-ranked, small slice on
            // the server) so we render the whole result; non-search list views
            // are paginated by LibraryPageSize so scrolling streams the rest.
            var slice = hasSearch ? metas : metas.Take(LibraryPageSize).ToList();

            return View(new LibraryViewModel
            {
                ConfigUid = uid,
                AnimeService = tokenData.anime_service,
                ActiveList = activeList,
                Tabs = UserListTypes,
                Search = hasSearch ? search.Trim() : null,
                Genre = hasGenre ? genre.Trim() : null,
                AvailableGenres = PopularGenres,
                Items = slice,
                TotalItems = total,
                Paginated = !hasSearch && total > slice.Count,
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
        /// Returns just the poster-grid HTML for the requested filter combo, so
        /// filter-search.js can swap the results pane in place instead of
        /// triggering a full page reload when the user clicks Search / changes
        /// the genre dropdown. Mirrors Index's data-fetch verbatim — same
        /// per-service dispatch, same search-relevance re-rank, same cache
        /// bypass behaviour. Anonymous users are bounced (the JS shouldn't
        /// hit this for them, but defend in depth).
        /// </summary>
        [Route("/library/page")]
        public async Task<IActionResult> Page(string list = null, string search = null, string genre = null, int skip = 0, bool fullPane = false)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync();
            if (tokenData == null || tokenData.anonymousUser)
                return Unauthorized();

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

            var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);

            var listForCall = hasSearch ? ListType.Search : activeList;
            const bool groupSeasonsForCall = false;

            // Same no-cache, same per-service dispatch as Index. The slice
            // logic below trims to a single LibraryPageSize-sized window so
            // scroll appends only render their chunk; fullPane callers
            // (filter-search.js Search-button swap) get the first page only,
            // since search results are single-shot anyway.
            var metas = tokenData.anime_service switch
            {
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall),
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
            var total = metas.Count;

            // Slice for the requested window. fullPane swaps land back at the
            // first page (matches the Index render shape); scroll appends ask
            // for an offset and get just that window. Skip beyond the upstream
            // total returns empty — the JS reads that as end-of-catalog and
            // stops the observer.
            var slice = hasSearch
                ? metas
                : metas.Skip(Math.Max(0, skip)).Take(LibraryPageSize).ToList();

            var activeLabel = labels[activeList];
            var grid = new PosterGridViewModel
            {
                Items = slice,
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
            // shared pane partial (paginator wrapper + sentinel for list
            // views, plain grid for search) so library-pagination.js can
            // rebind against the freshly-rendered wrapper. Scroll appends
            // just want the grid chunk to splice into the existing one.
            if (fullPane)
            {
                return PartialView("_LibraryPane", new LibraryPaneViewModel
                {
                    Grid = grid,
                    ShowPaginator = !hasSearch && total > slice.Count,
                    ListSlug = list?.ToLower() ?? activeList.ToString().ToLower(),
                    Search = hasSearch ? search.Trim() : null,
                    Genre = hasGenre ? genre.Trim() : null,
                    Skip = slice.Count,
                });
            }
            return PartialView("_PosterGrid", grid);
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
        // Total items the upstream returned before slicing — used by the
        // view to seed the paginator's data-skip + sentinel-driven scroll.
        public int TotalItems { get; set; }
        // True when the rendered slice is smaller than the upstream total,
        // i.e. there's more to fetch via /library/page. Always false in
        // search mode (search returns a single relevance-ranked slice).
        public bool Paginated { get; set; }
    }
}
