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

            // Parse the ?list= query param; default to Current and silently coerce
            // unknown / non-user-list values rather than 400'ing — keeps shareable URLs
            // tolerant of typos and old links.
            var activeList = ParseListType(list);

            // Honor the "Hide unaired from Watching" site preference — only affects
            // ListType.Current, every other tab passes the flag through harmlessly.
            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideUnreleased = configuration?.hideUnreleasedFromWatching == true;

            // Library scopes search to the active tab's user list — typing
            // "naruto" on Watching surfaces only Naruto entries already in
            // Watching, not every Naruto in the catalog. So the upstream
            // dispatch always uses activeList (Watching/Completed/etc.); the
            // search term is applied as an in-memory post-filter below.
            var listForCall = activeList;
            const bool groupSeasonsForCall = false;

            // No caching on the library path — each load reflects live state
            // off the upstream service. Each per-service call returns the
            // user's FULL list for the requested status; we don't paginate
            // server-side and don't slice — render every card. Earlier code
            // used a server-side scroll-fetch (sliced 50 entries per page,
            // sentinel-driven), but each /library/page call had to re-fetch
            // the entire list from upstream just to throw away everything
            // outside the requested window, so the pagination saved no
            // upstream work — only the HTML size. The browser handles a
            // few-hundred-card grid fine.
            var metas = tokenData.anime_service switch
            {
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listForCall, genre: genre, groupSeasons: groupSeasonsForCall, hideUnreleased: hideUnreleased),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listForCall, genre: genre, groupSeasons: groupSeasonsForCall, hideUnreleased: hideUnreleased),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, listForCall, genre: genre, groupSeasons: groupSeasonsForCall, hideUnreleased: hideUnreleased),
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
            // Alphabetical sort on non-search list views. Search keeps its
            // relevance ranking (sorted by ScoreMatch above) since
            // alphabetical ordering would bury the obvious matches under
            // "Aharen-san" / "Akira" / etc.
            if (!hasSearch)
            {
                metas = metas
                    .OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
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
                Items = metas,
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
        /// Returns the library pane partial for the requested filter combo —
        /// used by filter-search.js to swap the results pane in place when
        /// the user clicks Search / changes genre / etc., without a full
        /// page reload. Mirrors Index's data-fetch verbatim (same per-
        /// service dispatch, same search-relevance re-rank, same alphabetical
        /// sort, no cache, no slicing). Anonymous users are bounced.
        /// </summary>
        [Route("/library/page")]
        public async Task<IActionResult> Page(string list = null, string search = null, string genre = null)
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

            var listForCall = activeList;
            const bool groupSeasonsForCall = false;

            var metas = tokenData.anime_service switch
            {
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listForCall, genre: genre, groupSeasons: groupSeasonsForCall, hideUnreleased: hideUnreleased),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listForCall, genre: genre, groupSeasons: groupSeasonsForCall, hideUnreleased: hideUnreleased),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, listForCall, genre: genre, groupSeasons: groupSeasonsForCall, hideUnreleased: hideUnreleased),
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
            if (!hasSearch)
            {
                metas = metas
                    .OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var activeLabel = labels[activeList];
            return PartialView("_LibraryPane", new LibraryPaneViewModel
            {
                Grid = new PosterGridViewModel
                {
                    Items = metas,
                    ConfigUid = uid,
                    EmptyMessage = hasSearch
                        ? $"No matches for \"{search.Trim()}\"."
                        : hasGenre
                            ? $"No {genre.Trim().ToLower()} anime in your {activeLabel} list."
                            : $"Nothing in your {activeLabel} list on {tokenData.anime_service}.",
                },
            });
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
