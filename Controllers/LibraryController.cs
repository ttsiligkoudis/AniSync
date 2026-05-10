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

        [Route("/library")]
        public async Task<IActionResult> Index(string list = null, string search = null)
        {
            // Session-based identity. /library has no path-config — it's pure web app.
            // Anonymous visitors and not-logged-in sessions get bounced to the dashboard
            // because user lists require a real list account; the dashboard already
            // explains that and offers a Configure-page CTA where they can log in.
            var tokenData = await _tokenService.GetAccessTokenAsync();
            if (tokenData == null || tokenData.anonymousUser)
                return RedirectToAction("Index", "Home");

            var hasSearch = !string.IsNullOrWhiteSpace(search);

            // Parse the ?list= query param; default to Current and silently coerce
            // unknown / non-user-list values rather than 400'ing — keeps shareable URLs
            // tolerant of typos and old links.
            var activeList = ParseListType(list);

            // Resolve the row's UID so the Manage Entry links on each card can use the
            // existing config-scoped /{config}/Meta/ManageEntry/{id} flow without us
            // having to surface the UID in the visible URL — it stays an implementation
            // detail of the link href.
            var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);

            // Honour the user's "Group anime seasons" toggle (disableSeasonGrouping
            // bit) so the web-app library matches what the addon catalog shows on
            // Stremio. Defaults to grouped (toggle ON) when no flags are stored.
            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var groupSeasonsFromConfig = configuration?.disableSeasonGrouping != true;

            // Search runs as ListType.Search across the full anime database (mirrors
            // the addon's /catalog/.../Search behaviour); the active tab is ignored
            // while a search query is present. groupSeasons=false here too, matching
            // CatalogController's search branch — collapsing seasons in search rewrites
            // titles to the shortest variant which is wrong for "find this specific
            // anime" intent. Same dispatch table either way.
            var listForCall = hasSearch ? ListType.Search : activeList;
            var groupSeasonsForCall = !hasSearch && groupSeasonsFromConfig;
            var metas = tokenData.anime_service switch
            {
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listForCall, search: search, groupSeasons: groupSeasonsForCall),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listForCall, search: search, groupSeasons: groupSeasonsForCall),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, listForCall, search: search, groupSeasons: groupSeasonsForCall),
            };

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
        public List<Meta> Items { get; set; } = [];
    }
}
