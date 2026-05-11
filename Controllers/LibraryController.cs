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
        private readonly IUserListCache _listCache;

        public LibraryController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IConfigStore configStore,
            IUserListCache listCache)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
            _listCache = listCache;
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
        public async Task<IActionResult> Index(string list = null, string search = null, string genre = null, bool nocache = false)
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

            // The per-service dispatch lambda — captured by GetOrFetchAsync so the
            // upstream call only fires on cache miss / bypass. Search paths skip
            // the cache automatically (UserListCache treats ListType.Search as
            // non-cacheable) so the same call shape handles both modes.
            async Task<List<Meta>> FetchActiveListAsync() => tokenData.anime_service switch
            {
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall),
            };

            // Bypass the per-user list cache when a genre filter is active —
            // UserListCache keys on (uid, service, listType, groupSeasons) and
            // doesn't differentiate by genre, so a cached "Current+ungrouped"
            // result would leak into "Current+ungrouped+Action" and vice
            // versa. The filtering happens post-fetch inside the service
            // calls anyway, so the unfiltered cache stays correct for the
            // common (no-genre) case.
            var metas = hasGenre
                ? await FetchActiveListAsync()
                : await _listCache.GetOrFetchAsync(tokenData, listForCall, groupSeasonsForCall,
                    FetchActiveListAsync, bypassCache: nocache);

            // Re-rank search results by Utils.ScoreMatch with the same 0.4 threshold
            // CatalogController applies — keeps the relevance ordering consistent
            // between the Stremio-side and web-app search experiences.
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

            return View(new LibraryViewModel
            {
                ConfigUid = uid,
                AnimeService = tokenData.anime_service,
                ActiveList = activeList,
                Tabs = UserListTypes,
                Search = hasSearch ? search.Trim() : null,
                Genre = hasGenre ? genre.Trim() : null,
                AvailableGenres = PopularGenres,
                Items = metas ?? [],
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
    }
}
