using AnimeList.Models;
using AnimeList.Services;
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
        private readonly ICinemetaService _cinemeta;
        private readonly ITraktService _trakt;
        private readonly IConfigStore _configStore;

        public DiscoverController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IAnilistFallback anilistFallback,
            ICinemetaService cinemeta,
            ITraktService trakt,
            IConfigStore configStore)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _anilistFallback = anilistFallback;
            _cinemeta = cinemeta;
            _trakt = trakt;
            _configStore = configStore;
        }

        // Hand-curated video genre picker (intersection of Cinemeta's movie / series
        // genre lists), used when Discover renders the movies / series browse — folded
        // in from the old standalone video controller now that /movies · /series are gone.
        private static readonly string[] VideoGenres =
        [
            "Action", "Adventure", "Animation", "Comedy", "Crime",
            "Documentary", "Drama", "Family", "Fantasy", "History",
            "Horror", "Mystery", "Romance", "Sci-Fi", "Thriller", "War",
        ];

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
        public async Task<IActionResult> Index(string list = null, string search = null, string genre = null, string season = null, string tag = null, string mode = null)
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

            // Media-type preference: Discover IS the browse surface for movies / series too
            // (the standalone /movies · /series routes are gone). For a video preference we
            // render the Cinemeta-backed browse view right here. Anonymous users (uid null)
            // always get anime — they can't have set a preference.
            if (uid != null)
            {
                var preferredMediaType = await _configStore.GetMediaTypeAsync(uid);
                if (preferredMediaType != MetaType.anime)
                    return await VideoBrowseAsync(preferredMediaType, uid, search, genre, mode);
            }

            // Honor the "Hide unaired from Watching" pref. Only affects the
            // ListType.Current discover-only tab; harmless on every other list.
            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideUnreleased = configuration?.hideUnreleasedFromWatching == true;
            // 18+ gate — default-zero installs and anonymous viewers get
            // family-safe results; users opt in via /configure Preferences.
            var hideAdult = configuration?.showAdultContent != true;

            // enableSeasonGrouping is now a general pref (not addon-only) and
            // Discover mirrors the same gating CatalogController uses for the
            // Stremio side: Search always runs ungrouped because the per-
            // service dedup would otherwise rewrite a movie's name to the
            // shortest among entries sharing an IMDb id; every other catalog
            // honors the user's toggle so a grouped install collapses
            // franchises consistently across web + addon.
            var listForCall = hasSearch ? ListType.Search : activeList;
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var groupSeasonsForCall = listForCall == ListType.Search ? false : groupSeasons;
            // Tag is an AniList-native filter — MAL/Kitsu don't expose an
            // equivalent — so when a tag is requested we route through
            // AnilistFallback's anonymous browse regardless of the viewer's
            // primary service. Cards still translate ids into the user's
            // primary's space so Manage Entry / detail-page hand-offs work.
            //
            // Kitsu's /anime?filter[season] endpoint silently ignores
            // page[offset] — every "next" page returns the same 20 entries.
            // Re-route Kitsu users' seasonal browse through AniList's
            // anonymous query (seasonal data is a global catalog, not
            // user-scoped) and translate the anilist:N ids back into
            // kitsu:M for click-through so Manage Entry still lands on
            // Kitsu's detail page.
            var routeSeasonalViaAnilist = listForCall == ListType.Seasonal
                && tokenData.anime_service == AnimeService.Kitsu;
            // Discover lists are all catalog reads (no user state), so when
            // AniList — the primary — is down we can serve them from Kitsu
            // anonymously. The cards will link to kitsu:N for the duration of
            // the outage; click-through still works because /anime/{id} dispatches
            // on the prefix. Tag routing already goes through AniList unconditionally;
            // skip the fallback there because Kitsu doesn't expose the same tag taxonomy.
            var fallbackToKitsu = tokenData.anime_service == AnimeService.Anilist
                && AnilistHealthMonitor.IsDown
                && !hasTag
                && !routeSeasonalViaAnilist;
            var catalogService = fallbackToKitsu ? AnimeService.Kitsu : tokenData.anime_service;
            var catalogToken = fallbackToKitsu
                ? new TokenData { anime_service = AnimeService.Kitsu }
                : tokenData;
            // Browse views (Trending / Seasonal / Airing / Tag) skip the
            // upstream catalog fetch on the initial render — discover-
            // pagination.js fires a page-1 fetch against /discover/page
            // immediately on load and swaps skeleton placeholders for the
            // real cards. Avoids holding the initial paint behind AniList
            // when the same data is going to be fetched anyway one beat
            // later. Search keeps server-side rendering because its
            // relevance ranking shares the same upstream call.
            List<Meta> metas;
            if (!hasSearch)
            {
                metas = new List<Meta>();
            }
            else if (hasTag)
            {
                metas = await _anilistFallback.GetByTagAsync(tag, tokenData.anime_service, hideAdult: hideAdult, groupSeasons: groupSeasons);
            }
            else if (routeSeasonalViaAnilist)
            {
                metas = await _anilistService.GetAnimeListAsync(
                    tokenData: null, listForCall,
                    search: search, genre: genre,
                    groupSeasons: groupSeasonsForCall, season: season,
                    hideUnreleased: hideUnreleased, hideAdult: hideAdult);
                metas = await _anilistFallback.TranslateMetaIdsAsync(metas, AnimeService.Kitsu, groupSeasons);
            }
            else
            {
                metas = catalogService switch
                {
                    AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(catalogToken, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(catalogToken, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                    _                        => await _kitsuService.GetAnimeListAsync(catalogToken, listForCall, search: search, genre: genre, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
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

                // Search runs ungrouped at the service layer to avoid the
                // dedup name-flatten that would mangle movie titles sharing
                // an IMDb id with a series. Post-rank, fold cours of the
                // same franchise into one card when the user has grouping
                // on — keeps the result list short and consistent with what
                // every other surface shows.
                if (groupSeasons)
                {
                    metas = await _anilistFallback.ApplyGroupingToMetasAsync(metas, groupSeasons: true);
                    metas = metas
                        .GroupBy(m => m.id, StringComparer.Ordinal)
                        .Select(g => g.First())
                        .ToList();
                }
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
                NeedsClientLoad = !hasSearch,
            });
        }

        /// <summary>
        /// Renders the Cinemeta-backed movies / series browse as the Discover surface for
        /// a video media-type preference. Search renders server-side; the popularity browse
        /// hands page 1 to discover-pagination.js (which pages via /video/page). Folded in
        /// from the retired standalone /movies · /series controller.
        /// </summary>
        private async Task<IActionResult> VideoBrowseAsync(MetaType mediaType, string uid, string search, string genre, string mode)
        {
            var type = mediaType == MetaType.movie ? "movie" : "series";
            var hasSearch = !string.IsNullOrWhiteSpace(search);

            // Trakt connection status for the header strip — cheap projection read, display only.
            var traktToken = await _configStore.GetTraktTokenAsync(uid);
            var traktConnected = traktToken?.Connected == true;

            // Available browse modes: Popular (Cinemeta) is always present; the
            // Trakt feeds appear when Trakt is configured, and "For You" only
            // when the user has Trakt connected.
            var modes = new List<(string Slug, string Label)> { ("popular", "Popular") };
            if (_trakt.IsConfigured)
            {
                modes.Add(("trending", "Trending"));
                modes.Add(("anticipated", "Anticipated"));
                modes.Add(("watched", "Most Watched"));
                if (traktConnected) modes.Add(("recommended", "For You"));
            }
            // Resolve the active mode; fall back to Popular for anything unknown
            // or not currently available to this user.
            var activeMode = modes.Any(m => m.Slug == mode) ? mode : "popular";

            var items = hasSearch
                ? await _cinemeta.GetVideoCatalogAsync(type, genre, search.Trim())
                : new List<Meta>();

            return View("/Views/Video/Index.cshtml", new VideoBrowseViewModel
            {
                Type = type,
                ConfigUid = uid,
                Genre = string.IsNullOrWhiteSpace(genre) ? null : genre.Trim(),
                Search = hasSearch ? search.Trim() : null,
                AvailableGenres = VideoGenres,
                Items = items,
                NeedsClientLoad = !hasSearch,
                Mode = activeMode,
                Modes = modes,
                SignedIn = true,
                TraktConfigured = _trakt.IsConfigured,
                TraktConnected = traktConnected,
                TraktUsername = traktToken?.username,
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
        public async Task<IActionResult> Page(string list, string genre = null, int page = 1, string season = null, string search = null, string tag = null, bool fullPane = false)
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
            // Same gate as Index — see comment there.
            var hideAdult = configuration?.showAdultContent != true;

            // Search runs as ListType.Search across the full anime database;
            // matches the addon-side branch. Pagination scroll calls don't
            // pass a search term so this stays the existing per-tab dispatch.
            // Same enableSeasonGrouping branching as Index above — Search is
            // pinned to ungrouped to avoid dedup name-flattening; every other
            // list reflects the user's toggle.
            var listForCall = hasSearch ? ListType.Search : activeList;
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var groupSeasonsForCall = listForCall == ListType.Search ? false : groupSeasons;
            var hasTag = !string.IsNullOrWhiteSpace(tag);

            // Kitsu's /anime?filter[season] endpoint silently ignores
            // page[offset] — every "next" page returns the same 20 entries
            // — so route Kitsu users' seasonal scrolls through AniList's
            // anonymous browse instead. The skip math below uses AniList's
            // 50-item page size in that case so the offset maps to AniList
            // pages cleanly. See the matching branch in Index() for the
            // initial render.
            var routeSeasonalViaAnilist = listForCall == ListType.Seasonal
                && tokenData.anime_service == AnimeService.Kitsu;
            // Same fallback gate as Index() — when AniList is the primary and
            // the upstream is down, swap to Kitsu for the duration of the outage.
            var fallbackToKitsu = tokenData.anime_service == AnimeService.Anilist
                && AnilistHealthMonitor.IsDown
                && !hasTag
                && !routeSeasonalViaAnilist;
            var catalogService = fallbackToKitsu ? AnimeService.Kitsu : tokenData.anime_service;
            var catalogToken = fallbackToKitsu
                ? new TokenData { anime_service = AnimeService.Kitsu }
                : tokenData;

            // Per-service translation. The services internally accept an
            // item-count skip (matching the Stremio addon's catalog-extras
            // semantics, which CatalogController shares this path with),
            // so we convert the 1-indexed page → item offset using the
            // active service's catalog page size. Hardcoding 20 / 50 here
            // duplicates a constant that lives on each service, but the
            // alternative is exposing CatalogPageSize through every
            // service interface for one consumer.
            var pageSize = (catalogService == AnimeService.Kitsu && !routeSeasonalViaAnilist) ? 20 : 50;
            var skip = page <= 1 ? null : ((page - 1) * pageSize).ToString();
            if (page < 1) page = 1;

            List<Meta> metas;
            if (hasTag)
            {
                // Mirror Index's tag-routing: tag filtering goes through the
                // AniList anonymous browse regardless of primary.
                metas = await _anilistFallback.GetByTagAsync(tag, tokenData.anime_service, skip, hideAdult: hideAdult, groupSeasons: groupSeasons);
            }
            else if (routeSeasonalViaAnilist)
            {
                metas = await _anilistService.GetAnimeListAsync(
                    tokenData: null, listForCall,
                    skip: skip, genre: genre, search: search,
                    groupSeasons: groupSeasonsForCall, season: season,
                    hideUnreleased: hideUnreleased, hideAdult: hideAdult);
                metas = await _anilistFallback.TranslateMetaIdsAsync(metas, AnimeService.Kitsu, groupSeasons);
            }
            else
            {
                metas = catalogService switch
                {
                    AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(catalogToken, listForCall, skip: skip, genre: genre, search: search, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(catalogToken, listForCall, skip: skip, genre: genre, search: search, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                    _                        => await _kitsuService.GetAnimeListAsync(catalogToken, listForCall, skip: skip, genre: genre, search: search, groupSeasons: groupSeasonsForCall, season: season, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
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

                // Same post-rank grouping pass Index() runs — fold cours
                // with a shared IMDb id when the user has grouping on.
                if (groupSeasons)
                {
                    metas = await _anilistFallback.ApplyGroupingToMetasAsync(metas, groupSeasons: true);
                    metas = metas
                        .GroupBy(m => m.id, StringComparer.Ordinal)
                        .Select(g => g.First())
                        .ToList();
                }
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
        public async Task<IActionResult> Studios(string search = null)
        {
            ViewData["StudioSearch"] = search;
            // Skip the upstream studios fetch on the initial browse
            // render — studio-pagination.js kicks an immediate page-1
            // call on load and swaps the skeleton placeholders for
            // real tiles. Mirrors the /discover catalog-browse path so
            // the page paints right away rather than holding behind
            // AniList. Search stays server-rendered so the "No studios
            // match …" hint can fire when the query returns nothing.
            if (string.IsNullOrWhiteSpace(search))
            {
                ViewData["StudiosNeedsClientLoad"] = true;
                return View("Studios", new List<StudioSummary>());
            }
            var (studios, _) = await _anilistFallback.GetStudiosListAsync(page: 1, search: search);
            return View("Studios", studios);
        }

        [Route("/discover/studio/page")]
        public async Task<IActionResult> StudiosPage(int page = 1, string search = null)
        {
            var (studios, hasNext) = await _anilistFallback.GetStudiosListAsync(page, search);
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

            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideAdult = configuration?.showAdultContent != true;
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var (name, items, _) = await _anilistFallback.GetStudioMediaAsync(id, tokenData.anime_service, hideAdult: hideAdult, groupSeasons: groupSeasons);
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

            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideAdult = configuration?.showAdultContent != true;
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var (_, items, hasNext) = await _anilistFallback.GetStudioMediaAsync(id, tokenData.anime_service, page, hideAdult: hideAdult, groupSeasons: groupSeasons);
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

            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideAdult = configuration?.showAdultContent != true;
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var (name, items) = await _anilistFallback.GetStaffMediaAsync(id, tokenData.anime_service, hideAdult: hideAdult, groupSeasons: groupSeasons);

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

            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideAdult = configuration?.showAdultContent != true;
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var (items, _) = await _anilistFallback.GetByTagPageAsync(tagStr, tokenData.anime_service, page: 1, hideAdult: hideAdult, groupSeasons: groupSeasons);
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

            var configuration = await GetConfigByUidAsync(uid, _configStore);
            var hideAdult = configuration?.showAdultContent != true;
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var (items, hasNext) = await _anilistFallback.GetByTagPageAsync(tagStr, tokenData.anime_service, page, hideAdult: hideAdult, groupSeasons: groupSeasons);
            Response.Headers["X-Has-Next-Page"] = hasNext ? "true" : "false";
            return PartialView("_PosterGrid", new PosterGridViewModel
            {
                Items = items ?? new List<Meta>(),
                ConfigUid = uid,
            });
        }

        // ===== Video (movie / series) browse data endpoints =====
        // The /discover/{movies,series} browse view (Views/Video/Index.cshtml)
        // is rendered by VideoBrowseAsync above; these are its client-loaded
        // data feeds, moved here from the removed VideoController so Discover
        // owns the whole browse surface.

        // Cinemeta pages its catalogs in blocks of 100. The browse JS sends
        // 1-indexed page numbers; we convert page → item offset with this.
        private const int CatalogPageSize = 100;
        // Trakt discovery modes page in small chunks — each item costs a Cinemeta
        // meta lookup for its poster, so keep the per-page fan-out bounded.
        private const int VideoModeSize = 20;

        /// <summary>
        /// Infinite-scroll pagination endpoint for the browse grids. Returns
        /// just the next chunk of poster cards via the shared _PosterGrid
        /// partial (VideoLinks on so cards route to /meta/{id}?type=…).
        /// </summary>
        [Route("/video/page")]
        public async Task<IActionResult> VideoPage(string type, string genre = null, int page = 1, string mode = null)
        {
            if (type != "movie" && type != "series") type = "movie";
            if (page < 1) page = 1;

            var uid = await ResolveVideoUidAsync();

            List<Meta> items;
            if (mode is "trending" or "anticipated" or "watched" or "recommended")
            {
                // "recommended" needs a connected user; all Trakt feeds need a
                // configured client. Bail to an empty page (paginator stops) when
                // the request can't be served, so the grid ends cleanly.
                if (!_trakt.IsConfigured || (mode == "recommended" && string.IsNullOrEmpty(uid)))
                {
                    items = new List<Meta>();
                }
                else
                {
                    var traktItems = await _trakt.GetDiscoveryAsync(uid, type, mode, genre, page, VideoModeSize);
                    items = await BuildVideoMetasAsync(traktItems);
                }
            }
            else
            {
                // Popular (default) — Cinemeta's "top" catalog, paged by skip.
                var skip = (page - 1) * CatalogPageSize;
                items = await _cinemeta.GetVideoCatalogAsync(type, genre, search: null, skip: skip);
            }

            return PartialView("_PosterGrid", new PosterGridViewModel
            {
                Items = items,
                ConfigUid = uid,
                VideoLinks = true,
            });
        }

        // Hydrates Trakt list items (imdb id + type) into poster-bearing Meta via
        // Cinemeta, in parallel, preserving Trakt's ranked order and dropping any
        // id Cinemeta can't resolve. Forces Meta.type from the Trakt item so the
        // _PosterGrid VideoLinks routing picks the right ?type=.
        private async Task<List<Meta>> BuildVideoMetasAsync(List<TraktListItem> items)
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

        // UID resolution mirroring Index() — anonymous visitors get null.
        private async Task<string> ResolveVideoUidAsync()
        {
            var tokenData = await _tokenService.GetAccessTokenAsync();
            if (tokenData == null || tokenData.anonymousUser) return null;
            var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
            return resolved;
        }
    }

    /// <summary>
    /// View model for the video browse pages (Views/Video/Index.cshtml).
    /// Type is the Cinemeta content type ("movie" / "series") and drives the
    /// page title, active-nav highlight and the pagination endpoint's type
    /// parameter.
    /// </summary>
    public class VideoBrowseViewModel
    {
        public string Type { get; set; }
        public string ConfigUid { get; set; }
        public string Genre { get; set; }
        public string Search { get; set; }
        public IReadOnlyList<string> AvailableGenres { get; set; } = [];
        public List<Meta> Items { get; set; } = [];
        // True for the popularity browse — the view emits skeleton placeholders
        // and video-pagination.js fetches page 1 on load. False for search,
        // which is rendered server-side from Items.
        public bool NeedsClientLoad { get; set; }

        // Active browse mode slug (popular | trending | anticipated | watched |
        // recommended) and the modes available to this user (Popular always;
        // Trakt feeds when configured; "For You" when connected).
        public string Mode { get; set; } = "popular";
        public IReadOnlyList<(string Slug, string Label)> Modes { get; set; } = [];

        // Trakt connection status for the header strip.
        public bool SignedIn { get; set; }
        public bool TraktConfigured { get; set; }
        public bool TraktConnected { get; set; }
        public string TraktUsername { get; set; }
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

        /// <summary>
        /// Signals the view to skip the server-side first-page render and
        /// emit skeleton placeholders instead — discover-pagination.js then
        /// fetches /discover/page?page=1 on load and swaps them for real
        /// cards. Set on every browse load (Trending / Seasonal / Airing /
        /// Tag) so the initial paint isn't blocked on AniList. Search
        /// keeps server-side rendering because relevance ranking happens
        /// in the same round-trip the data fetch does.
        /// </summary>
        public bool NeedsClientLoad { get; set; }
    }

    /// <summary>
    /// Slim model for the shared _DiscoverTabs partial. The tab strip is rendered
    /// on the catalog Index view (Trending / Seasonal / Airing) and on the
    /// Browse-by-Studio / Browse-by-Tag list pages — same five pills, with the
    /// active one varying by surface. Index passes through the genre / search /
    /// season currently dialled in so the catalog tabs preserve filters across
    /// clicks; Studios / Tags pages leave those null since they don't share the
    /// filter surface.
    /// </summary>
    public class DiscoverTabsViewModel
    {
        // Active catalog tab, or null when the active surface is a Browse-by page.
        public ListType? ActiveList { get; set; }
        // Active Browse-by surface — "studios" / "tags" — null on the catalog view.
        public string ActiveBrowse { get; set; }
        public string Genre { get; set; }
        public string Search { get; set; }
        public string Season { get; set; }
    }
}
