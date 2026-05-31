using AnimeList.Models;
using AnimeList.Services;
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
        private readonly IConfigStore _configStore;
        private readonly IMergedListService _mergedListService;
        private readonly ITraktService _traktService;
        private readonly ICinemetaService _cinemeta;

        public LibraryController(
            ITokenService tokenService,
            IConfigStore configStore,
            IMergedListService mergedListService,
            ITraktService traktService,
            ICinemetaService cinemeta)
        {
            _tokenService = tokenService;
            _configStore = configStore;
            _mergedListService = mergedListService;
            _traktService = traktService;
            _cinemeta = cinemeta;
        }

        // The list tabs that make sense for movies / series (sourced from Trakt):
        // Watchlist (Planning), Watched history (Completed), and in-progress playback
        // (Current). The anime-only Paused / Dropped / Rewatching tabs have no Trakt
        // analogue, so the video library shows a reduced tab strip.
        // Video (Trakt) now supports every status too: Planning → watchlist,
        // Watching → playback, Completed → history, and Paused / Dropped /
        // Repeating → AniSync-managed Trakt personal lists.
        private static readonly ListType[] VideoListTypes =
        [
            ListType.Current,
            ListType.Completed,
            ListType.Planning,
            ListType.Paused,
            ListType.Dropped,
            ListType.Repeating,
        ];

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

            // Media-type preference decides what the library shows: anime (the user's
            // merged tracker lists) or movies / series (their Trakt lists). The tab strip
            // narrows for video since the anime-only statuses don't map onto Trakt.
            var enabledModes = await MediaTypePreference.ResolveEnabledAsync(HttpContext, uid, _configStore);
            var mediaType = MediaTypePreference.ResolveActive(HttpContext, enabledModes);
            ViewData["MtEnabled"] = enabledModes;   // drives the media-type toggle's chips
            var isVideo = mediaType != MetaType.anime;
            var tabs = isVideo ? VideoListTypes : UserListTypes;

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
            // 18+ gate — when off, the library hides any R18 / nsfw=black /
            // isAdult entries the user has in their list. Same site pref the
            // Discover and Stremio surfaces respect, so toggling once carries
            // through every browsing view.
            var hideAdult = configuration?.showAdultContent != true;

            // Library scopes search to the active tab's user list — typing
            // "naruto" on Watching surfaces only Naruto entries already in
            // Watching, not every Naruto in the catalog. So the upstream
            // dispatch always uses activeList (Watching/Completed/etc.); the
            // search term is applied as an in-memory post-filter below.
            var listForCall = activeList;
            // enableSeasonGrouping is the same general pref Stremio's catalog
            // honors; the web library used to hardcode false here but the
            // toggle is no longer addon-only — when on, multi-cour franchises
            // collapse to a single IMDb-id card across every list surface.
            var groupSeasonsForCall = configuration?.enableSeasonGrouping == true;

            // Skip the upstream fetch on the initial /library render —
            // filter-search.js auto-fires a submit on script load (the same
            // pipeline a real Search-button click takes) which hits
            // /library/page and drops the result into the pane. Avoids
            // holding the initial paint behind AniList / MAL / Kitsu's
            // list query. /library/page itself still does the live fetch
            // verbatim; nothing about the per-service dispatch changes.
            return View(new LibraryViewModel
            {
                ConfigUid = uid,
                AnimeService = tokenData.anime_service,
                MediaType = mediaType,
                ActiveList = activeList,
                Tabs = tabs,
                Search = hasSearch ? search.Trim() : null,
                Genre = hasGenre ? genre.Trim() : null,
                AvailableGenres = PopularGenres,
                Items = new List<Meta>(),
                NeedsClientLoad = true,
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

            // Movies / series come from Trakt (watchlist / history / playback) hydrated to
            // posters via Cinemeta, rather than the anime merged-list path below.
            var mediaType = await MediaTypePreference.ResolveActiveAsync(HttpContext, uid, _configStore);
            if (mediaType != MetaType.anime)
                return await VideoPaneAsync(uid, activeList, mediaType, search, hasSearch);

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
            var hideAdult = configuration?.showAdultContent != true;

            var listForCall = activeList;
            // Same enableSeasonGrouping pref Index() reads above — the inline
            // partial-refresh path needs to honor it too or the grouping would
            // flip whenever the user used the filter/search input.
            var groupSeasonsForCall = configuration?.enableSeasonGrouping == true;

            // Fan out to primary + every healthy linked secondary in parallel
            // so /library surfaces anime the user has on AniList / MAL / Kitsu
            // even when they're not on the primary. Same anime fetched from
            // multiple providers is deduped via the cross-service mapping;
            // the merge rules live in MergedListService (shared with the
            // dashboard's Continue Watching shelf).
            var metas = await _mergedListService.GetMergedListAsync(tokenData, uid, listForCall, genre,
                groupSeasonsForCall, hideUnreleased, hideAdult);

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

        // Movies / series library pane: pull the user's Trakt list for the active tab,
        // hydrate the IMDb ids into poster-bearing Metas via Cinemeta (in parallel,
        // preserving Trakt order), and render the same _LibraryPane the anime path uses —
        // with VideoLinks on so the cards route to /movie|series/{id}.
        private async Task<IActionResult> VideoPaneAsync(string uid, ListType activeList, MetaType mediaType, string search, bool hasSearch)
        {
            var labels = new Dictionary<ListType, string>
            {
                [ListType.Current]   = "Continue Watching",
                [ListType.Completed] = "Watched",
                [ListType.Planning]  = "Watchlist",
            };
            var kind = mediaType == MetaType.movie ? "movies" : "series";

            var items = await _traktService.GetListAsync(uid, activeList, mediaType);
            var metas = await HydrateVideoMetasAsync(items);

            if (hasSearch && metas.Count > 0)
            {
                var q = NormalizeTitle(search);
                metas = metas
                    .Select(m => (meta: m, score: ScoreMatch(q, m.name)))
                    .Where(x => x.score >= 0.4)
                    .OrderByDescending(x => x.score)
                    .Select(x => x.meta)
                    .ToList();
            }

            var label = labels.GetValueOrDefault(activeList, activeList.ToString());
            if (!_traktService.IsConfigured)
            {
                return PartialView("_LibraryPane", new LibraryPaneViewModel
                {
                    Grid = new PosterGridViewModel
                    {
                        Items = [],
                        ConfigUid = uid,
                        VideoLinks = true,
                        EmptyMessage = "Trakt isn't configured on this server, so movie / series lists aren't available.",
                    },
                });
            }

            return PartialView("_LibraryPane", new LibraryPaneViewModel
            {
                Grid = new PosterGridViewModel
                {
                    Items = metas,
                    ConfigUid = uid,
                    VideoLinks = true,
                    EmptyMessage = hasSearch
                        ? $"No matches for \"{search.Trim()}\"."
                        : $"Nothing in your {kind} {label} on Trakt. Connect Trakt from your Account to track {kind}.",
                },
            });
        }

        // Hydrates Trakt list items (imdb id + type) into poster-bearing Meta via Cinemeta,
        // in parallel, preserving order and dropping any id Cinemeta can't resolve. Forces
        // Meta.type from the Trakt item so _PosterGrid's VideoLinks routing picks the right
        // /movie|series path.
        private async Task<List<Meta>> HydrateVideoMetasAsync(List<TraktListItem> items)
        {
            var lookups = items.Select(async it =>
            {
                try
                {
                    var meta = await _cinemeta.GetVideoMetaAsync(it.Type, it.ImdbId);
                    if (meta == null) return null;
                    meta.type = it.Type == "movie" ? MetaType.movie.ToString() : MetaType.series.ToString();
                    return meta;
                }
                catch
                {
                    return null;
                }
            });
            var resolved = await Task.WhenAll(lookups);
            return resolved.Where(m => m != null).ToList();
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
        /// <summary>Preferred media type — anime renders the merged tracker lists; movie / series render Trakt lists.</summary>
        public MetaType MediaType { get; set; } = MetaType.anime;
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

        /// <summary>
        /// Signals the view to skip the server-side render and emit skeleton
        /// placeholders instead — filter-search.js then fires an immediate
        /// submit on load (matching the same flow a real Search-button click
        /// would take) and swaps the placeholders for /library/page's
        /// response. Set on every initial render; only filter submits keep
        /// rendering server-side.
        /// </summary>
        public bool NeedsClientLoad { get; set; }
    }
}
