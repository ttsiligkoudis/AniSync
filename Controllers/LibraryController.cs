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
        private readonly IAnimeMappingService _mappingService;
        private readonly ILogger<LibraryController> _logger;

        public LibraryController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IConfigStore configStore,
            IAnimeMappingService mappingService,
            ILogger<LibraryController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
            _mappingService = mappingService;
            _logger = logger;
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
                ActiveList = activeList,
                Tabs = UserListTypes,
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
            // see FetchMergedLibraryAsync for the merge rules.
            var metas = await FetchMergedLibraryAsync(tokenData, uid, listForCall, genre,
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

        /// <summary>
        /// Fetches the user's list for the requested status across the primary
        /// AND every healthy linked secondary, in parallel, then merges the
        /// results into a single deduped list. Same anime fetched from
        /// multiple providers collapses to one card via the cross-service
        /// mapping table — the dedup key is the primary's id when the
        /// mapping has one, falling back to a canonical AniList id for
        /// linked-only anime, and finally the raw service id when even
        /// AniList has no mapping. Per-provider failures degrade
        /// gracefully: a logged warning and an empty contribution from
        /// that provider, so an AniList outage doesn't blank the user's
        /// MAL + Kitsu cards too.
        /// </summary>
        private async Task<List<Meta>> FetchMergedLibraryAsync(
            TokenData primary, string uid, ListType listType, string genre,
            bool groupSeasons, bool hideUnreleased, bool hideAdult)
        {
            // Active linked tokens — same gate SyncService uses (skip
            // NeedsReauth + missing access_token entries so we don't fan
            // out into a guaranteed-failure call).
            var linked = await _configStore.GetLinkedTokensAsync(uid);
            var sources = new List<(TokenData Token, AnimeService Service)>
            {
                (primary, primary.anime_service),
            };
            foreach (var l in linked)
            {
                if (l.NeedsReauth || l.TokenData == null) continue;
                if (string.IsNullOrEmpty(l.TokenData.access_token)) continue;
                if (l.Service == primary.anime_service) continue;
                sources.Add((l.TokenData, l.Service));
            }

            // Per-provider fetch wrapped so one upstream's failure doesn't
            // poison the whole Task.WhenAll. Each entry returns an empty
            // list on exception and the merge step just skips it.
            async Task<List<Meta>> SafeFetch(TokenData token, AnimeService service)
            {
                try
                {
                    return service switch
                    {
                        AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(token, listType, genre: genre, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                        AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(token, listType, genre: genre, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                        _                        => await _kitsuService.GetAnimeListAsync(token, listType, genre: genre, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                    } ?? new List<Meta>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Library multi-provider fetch failed for {Service}.", service);
                    return new List<Meta>();
                }
            }

            var results = await Task.WhenAll(sources.Select(s => SafeFetch(s.Token, s.Service)));

            // Primary's batch is index 0 so its entries land first; collisions
            // from linked providers get skipped via the seen-set, which means
            // primary wins on overlap (status / progress / score the user has
            // on their primary list shows on the card, not the linked
            // provider's potentially stale copy).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Title-based safety net for the case the cross-service mapping
            // can't help with — same anime listed on both linked providers
            // with no shared id (donghua, simulcast-only originals, recently
            // licensed shows). For each kept entry we record a normalised
            // title token set + year + format, then compare every new
            // candidate against the kept ones before letting it through.
            // Pairs collapse when:
            //   - Jaccard token overlap is ≥ 0.7 (catches "World Trigger
            //     Reboot" ↔ "World Trigger REBOOT Project (Provisional
            //     Title)" — 3/4 = 0.75), AND
            //   - either both entries agree on year OR at least one is
            //     missing it (don't merge same-named different-year
            //     entries like a 2018 OVA and its 2024 remake), AND
            //   - same format guard (TV vs Movie remains distinct).
            // Threshold + guards are deliberately conservative — false
            // negatives (occasional duplicate slip-through) are way less
            // user-hostile than false positives (a real anime getting
            // hidden from the user's library).
            const double TITLE_SIMILARITY_THRESHOLD = 0.7;
            var titleSignatures = new List<(HashSet<string> Tokens, int? Year, string Format)>();
            var merged = new List<Meta>();
            var primaryService = primary.anime_service;

            for (var i = 0; i < results.Length; i++)
            {
                var batch = results[i];
                if (batch == null) continue;
                foreach (var m in batch)
                {
                    if (m == null || string.IsNullOrEmpty(m.id)) continue;

                    // Try mapping into the primary's id-space first — when
                    // it succeeds the card links to the canonical detail
                    // URL the rest of the app uses, and the dedup key is
                    // the primary id so same-anime entries from different
                    // providers collapse cleanly.
                    var primaryId = await _mappingService.GetIdWithPrefixAsync(m.id, primaryService);

                    string dedupKey;
                    if (!string.IsNullOrEmpty(primaryId))
                    {
                        dedupKey = primaryId;
                        if (primaryId != m.id) m.id = primaryId;
                    }
                    else
                    {
                        // No primary mapping. Fall back to a canonical
                        // AniList id so the same anime fetched from MAL +
                        // Kitsu (both with no primary-MAL row for this
                        // donghua / obscure show) still dedupes when an
                        // AniList row exists. Last resort uses the raw
                        // source id, which may not dedupe across providers
                        // but at least won't dedupe-DROP either.
                        var anilistId = primaryService == AnimeService.Anilist
                            ? null
                            : await _mappingService.GetIdWithPrefixAsync(m.id, AnimeService.Anilist);
                        dedupKey = anilistId ?? m.id;
                    }

                    if (!seen.Add(dedupKey)) continue;

                    // Mapping-dedup passed; run the title-similarity safety
                    // net before keeping the entry.
                    var normalized = NormalizeTitle(m.name ?? string.Empty);
                    var tokens = string.IsNullOrEmpty(normalized)
                        ? new HashSet<string>()
                        : new HashSet<string>(normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    var fuzzyDup = false;
                    if (tokens.Count > 0)
                    {
                        foreach (var prev in titleSignatures)
                        {
                            if (prev.Tokens.Count == 0) continue;
                            // Year guard — same name, different year is almost
                            // always different anime (OVA vs remake, original vs
                            // sequel sharing a base title).
                            if (m.year.HasValue && prev.Year.HasValue && m.year.Value != prev.Year.Value) continue;
                            // Format guard — only blocks a merge across the
                            // movie/not-movie line (a TV series and its
                            // theatrical recap genuinely belong as separate
                            // cards). Bucketed coarsely on purpose: providers
                            // disagree on the fine-grained label for the SAME
                            // show — Kitsu calls Monster Eater "TV" while
                            // AniList calls it "TV Short" — and a strict string
                            // compare let that duplicate slip through. TV / TV
                            // Short / ONA / OVA / Special all bucket to
                            // "series" so cross-provider label drift no longer
                            // defeats the dedup.
                            if (FormatBucket(m.format) is { } fb && FormatBucket(prev.Format) is { } pb
                                && !string.Equals(fb, pb, StringComparison.Ordinal)) continue;
                            var intersectCount = tokens.Intersect(prev.Tokens).Count();
                            if (intersectCount == 0) continue;
                            var unionCount = tokens.Union(prev.Tokens).Count();
                            var jaccard = (double)intersectCount / unionCount;
                            if (jaccard >= TITLE_SIMILARITY_THRESHOLD)
                            {
                                fuzzyDup = true;
                                break;
                            }
                        }
                    }
                    if (fuzzyDup) continue;

                    merged.Add(m);
                    titleSignatures.Add((tokens, m.year, m.format));
                }
            }
            return merged;
        }

        /// <summary>
        /// Collapses a per-provider format label into a coarse bucket for the
        /// cross-provider dedup's format guard. Only the movie/not-movie line
        /// matters there — a TV series and its theatrical recap are genuinely
        /// separate cards — so everything that isn't a movie (TV, TV Short,
        /// ONA, OVA, Special, …) buckets to "series". This is what stops
        /// providers' fine-grained label disagreements (Kitsu "TV" vs AniList
        /// "TV Short" for the same show) from defeating the dedup. Returns
        /// null for an absent format so the guard treats "unknown" as
        /// "could match anything".
        /// </summary>
        private static string FormatBucket(string format)
        {
            if (string.IsNullOrWhiteSpace(format)) return null;
            return format.ToLowerInvariant().Contains("movie") ? "movie" : "series";
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
