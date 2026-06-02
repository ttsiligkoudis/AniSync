using AnimeList.Models;
using AnimeList.Services;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;

public class HomeController : Controller
{
    // Maximum number of "Continue watching" tiles surfaced on the dashboard. Matches
    // the "Most Popular / Most Anticipated" shelves so the row reads as a full
    // horizontal scroller rather than a stubby 2-3 row block — the user can fan
    // through their backlog without leaving the dashboard.
    private const int ContinueWatchingMaxItems = 15;

    private readonly ITokenService _tokenService;
    private readonly IConfigStore _configStore;
    private readonly IAnilistService _anilistService;
    private readonly IAnilistFallback _anilistFallback;
    private readonly IAddonStreamService _addonStreamService;
    private readonly IUserListCache _listCache;
    private readonly IMemoryCache _dashboardCache;
    private readonly IMergedListService _mergedListService;
    // Video-mode dashboard shelves (movies / series) source from Cinemeta's
    // catalog + Trakt's discovery / playback feeds, mirroring how Discover's
    // video browse already works.
    private readonly ICinemetaService _cinemeta;
    private readonly ITraktService _trakt;

    // Day-stale rankings are indistinguishable from live ones for the
    // popularity shelves — same TTL the seasonal-stats cache uses inside
    // AnilistFallback. Kept private here because only the dashboard's
    // popular-by-season helper needs it.
    private static readonly TimeSpan PopularBySeasonCacheDuration = TimeSpan.FromHours(24);
    // Trending / Popular shelves: shorter than the seasonal cache because
    // Trending shifts through the day; Popular barely moves but a 6h refresh
    // is cheap and keeps both reasonably fresh.
    private static readonly TimeSpan CatalogShelfCacheDuration = TimeSpan.FromHours(6);

    public HomeController(
        ITokenService tokenService,
        IConfigStore configStore,
        IAnilistService anilistService,
        IAnilistFallback anilistFallback,
        IAddonStreamService addonStreamService,
        IUserListCache listCache,
        IMemoryCache dashboardCache,
        IMergedListService mergedListService,
        ICinemetaService cinemeta,
        ITraktService trakt)
    {
        _tokenService = tokenService;
        _configStore = configStore;
        _anilistService = anilistService;
        _anilistFallback = anilistFallback;
        _addonStreamService = addonStreamService;
        _listCache = listCache;
        _dashboardCache = dashboardCache;
        _mergedListService = mergedListService;
        _cinemeta = cinemeta;
        _trakt = trakt;
    }

    /// <summary>
    /// Unified error landing page. Reached three ways:
    ///   1. Status-code re-execute (Program.cs UseStatusCodePagesWithReExecute)
    ///      with the original code substituted into the {statusCode} slot —
    ///      catches any unhandled 4xx/5xx with no body, most commonly route
    ///      misses (404) and bare NotFound() / StatusCode(500) returns.
    ///   2. Exception handler middleware re-executes /error/500 for any
    ///      uncaught exception in non-dev builds.
    ///   3. Controllers that detect a missing entity at the top of an action
    ///      can return View("NotFound") directly, sidestepping the
    ///      re-execute round-trip.
    /// 404 gets the NotFound view; everything else falls through to
    /// StatusPage, which switches its copy on the status code so 401 /
    /// 403 / 405 / 429 / 5xx each get distinct messaging. The original
    /// status code is restored on the response so crawlers / share-link
    /// previewers see the real code even though we're returning a full
    /// HTML body.
    /// </summary>
    [Route("/error/{statusCode:int?}")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult ErrorPage(int? statusCode)
    {
        var code = statusCode ?? 500;
        Response.StatusCode = code;
        // 404 has its own dedicated copy (browse-anime CTA, distinct headline).
        // Everything else routes through StatusPage, which switches its message
        // based on Response.StatusCode so a rate-limited (429) or unauthorised
        // (401/403) user doesn't see a generic "server crashed" page.
        return View(code == 404 ? "NotFound" : "StatusPage");
    }

    public async Task<IActionResult> Index(bool nocache = false)
    {
        // The dashboard is the front door for the web app: anonymous visitors see
        // login CTAs, signed-in users see navigation tiles plus a small slice of
        // their Currently Watching list ("Continue watching"). Read session state
        // first so the view can branch — no DB hits / token refreshes / linked-merge
        // logic for the dashboard render itself. The configure page (which still
        // does all that) lives at /configure and remains where Stremio's manifest
        // points its "Configure" deep-link.
        var sessionStr = HttpContext.Session.GetString("AccessToken");
        TokenData tokenData = null;
        if (!string.IsNullOrEmpty(sessionStr))
            tokenData = DeserializeObject<TokenData>(sessionStr);

        string uid = null;
        bool hasStats = false;
        bool hasStreamAddons = false;
        List<string> contributingNames = [];
        // Lifted to outer scope so the view-model construction below can
        // surface the currently-working linked services alongside the
        // primary in the dashboard hero badge.
        List<LinkedToken> linkedTokens = [];

        // Continue-watching + stats surfaces only fire for non-anonymous logged-in
        // users. Anonymous and not-logged-in visitors get the plain three-tile
        // dashboard; they have nothing to "continue" and no list to compute stats
        // from.
        if (tokenData != null && !tokenData.anonymousUser)
        {
            var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
            uid = resolved;

            // Pick the AniList token (primary or linked) — stats now go
            // through AniList's User.statistics GraphQL, which is a single
            // query that's vastly cheaper than fetching the full Watching +
            // Completed lists. Users without an AniList account see the
            // stats panel hidden (HasStats = false); they can link AniList
            // from /configure to unlock it.
            TokenData anilistTokenForStats = null;
            if (!string.IsNullOrEmpty(uid))
            {
                linkedTokens = await _configStore.GetLinkedTokensAsync(uid);

                // Gate the "set up streaming" nudge banner: one lightweight
                // store read on a path that already touches the store
                // (FindUidByIdentityAsync / GetLinkedTokensAsync above), so
                // the dashboard render still reflects current config without
                // an AniList round-trip. The banner self-hides once this is
                // true. Same call the configure page uses (see ~line 340).
                var streamAddons = await _configStore.GetStreamAddonsAsync(uid);
                hasStreamAddons = streamAddons is { Count: > 0 };
            }
            if (tokenData.anime_service == AnimeService.Anilist)
            {
                anilistTokenForStats = tokenData;
            }
            else
            {
                foreach (var lt in linkedTokens)
                {
                    if (lt.NeedsReauth || lt.TokenData == null || lt.TokenData.anonymousUser) continue;
                    if (lt.Service != AnimeService.Anilist) continue;
                    anilistTokenForStats = lt.TokenData;
                    break;
                }
            }

            // Stats are no longer fetched server-side — the dashboard JS
            // hits /Home/AnilistStats from the client (with a 24 h
            // localStorage cache in front) so each dashboard render
            // doesn't pay the AniList round-trip. Only flag whether the
            // panel should render at all (user has a usable AniList
            // token); the actual numbers fill in via JS once the page
            // mounts.
            if (anilistTokenForStats != null)
            {
                hasStats = true;
                contributingNames.Add(AnimeService.Anilist.ToString());
            }

            // Continue Watching is fetched client-side from
            // /Home/ContinueWatchingData (returns a rendered _PosterGrid
            // partial) and cached in localStorage (anisync.continueWatching.v1,
            // 10 min TTL) so each dashboard render doesn't pay the per-user
            // list round-trip. The view renders the section structure
            // unconditionally for logged-in non-anonymous users and the
            // JS un-hides it once the HTML loads.
        }

        // The "This Season" stat strip and the discovery shelves (New
        // Episodes Today, Trending, Popular, Most Anticipated) used to
        // be fetched here and awaited via Task.WhenAll, which held the whole
        // dashboard render behind AniList. They're now fetched client-side
        // after first paint — each from its own /Home/*Data endpoint — so the
        // page paints instantly with shimmer placeholders and every shelf
        // (plus Continue Watching and Your-stats, already client-loaded)
        // loads independently and in parallel. See the inline loaders in
        // Views/Home/Index.cshtml and the shelf endpoints further down.

        // Surface the linked-secondary service names alongside the primary
        // so the dashboard hero pill can read "✓ Synced with AniList · MAL"
        // rather than just the primary. Quietly drops links flagged
        // NeedsReauth / anonymous / null-token so the badge only counts
        // currently-working connections.
        var linkedServiceNames = (tokenData != null && !tokenData.anonymousUser)
            ? linkedTokens
                .Where(lt => !lt.NeedsReauth && lt.TokenData != null && !lt.TokenData.anonymousUser)
                .Select(lt => lt.Service.ToString())
                .ToList()
            : [];

        // Resolve the viewer's modes. The dashboard COMBINES every enabled mode
        // (multi-selected in the chooser modal); the active single mode still
        // drives the hero copy + Browse By. Logged-in users' account setting +
        // the enabled-set cookie back this; anonymous visitors use cookies only.
        var enabledMediaTypes = await MediaTypePreference.ResolveEnabledAsync(HttpContext, uid, _configStore);
        var mediaType = MediaTypePreference.ResolveActive(HttpContext, enabledMediaTypes);

        // Video shelves ("Your stats" + Continue Watching) need a connected
        // Trakt account, same as the anime stats need AniList. One cheap
        // projection read, and only when a video mode is enabled for a real user.
        var traktConnected = false;
        if (!string.IsNullOrEmpty(uid) && enabledMediaTypes.Any(t => t != MetaType.anime))
        {
            var traktToken = await _configStore.GetTraktTokenAsync(uid);
            traktConnected = traktToken?.Connected == true;
        }

        return View(new DashboardViewModel
        {
            TokenData = tokenData,
            ConfigUid = uid,
            LinkedServices = linkedServiceNames,
            HasStats = hasStats,
            HasStreamAddons = hasStreamAddons,
            ContributingServices = contributingNames,
            MediaType = mediaType,
            EnabledMediaTypes = enabledMediaTypes,
            TraktConnected = traktConnected,
        });
    }

    // Cache-wrapping helper around the existing AnilistService catalog path.
    // Dispatches the same query the public Discover page uses — POPULARITY_DESC
    // sort over the season's media list — and slices to the top 15 for the
    // dashboard shelf. groupSeasons=false keeps each result's id in the
    // anilist:N space so AnimeController.Detail can resolve it per-user via
    // the cross-service mapping on click. Cache key includes the resolved
    // (season, year) tuple so the entry naturally rotates when AniList moves
    // to the next quarterly season; non-empty results only so a transient
    // upstream blip doesn't lock in an empty shelf for 24h.
    // Trending / Popular dashboard shelves — the same AniList catalog lists
    // the Discover Trending / Popular tabs use (TRENDING_DESC / POPULARITY_DESC),
    // fetched anonymously, top-15, cached globally on the anilist:N ids and
    // translated per-viewer into their primary's id space. Mirrors
    // GetPopularBySeasonAsync but keyed on the list type rather than a season.
    private async Task<List<Meta>> GetCatalogShelfAsync(ListType list, AnimeService translateTo, bool groupSeasons)
    {
        var cacheKey = $"dashboard:catalog:{list}";

        List<Meta> top;
        if (_dashboardCache.TryGetValue<List<Meta>>(cacheKey, out var cached) && cached != null)
        {
            top = cached;
        }
        else
        {
            try
            {
                var listResult = await _anilistService.GetAnimeListAsync(
                    tokenData: null,
                    list: list,
                    groupSeasons: false,
                    // Global cache → can't honor a per-user 18+ toggle, so keep the
                    // safer shape (same as GetPopularBySeasonAsync).
                    hideAdult: true);

                top = listResult?.Take(15).ToList() ?? [];

                if (top.Count > 0)
                {
                    _dashboardCache.Set(cacheKey, top, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = CatalogShelfCacheDuration,
                    });
                }
            }
            catch
            {
                top = [];
            }
        }

        return await _anilistFallback.TranslateMetaIdsAsync(top.Select(m => new Meta
        {
            id = m.id, name = m.name, poster = m.poster, type = m.type,
            score = m.score, episodes = m.episodes, year = m.year, format = m.format,
        }).ToList(), translateTo, groupSeasons);
    }

    private async Task<List<Meta>> GetPopularBySeasonAsync(string seasonOption, AnimeService translateTo = AnimeService.Anilist, bool groupSeasons = false)
    {
        var (season, year) = GetSeasonAndYear(seasonOption);
        var cacheKey = $"dashboard:popular-by-season:{season}:{year}";

        List<Meta> top;
        if (_dashboardCache.TryGetValue<List<Meta>>(cacheKey, out var cached) && cached != null)
        {
            top = cached;
        }
        else
        {
            try
            {
                var list = await _anilistService.GetAnimeListAsync(
                    tokenData: null,
                    list: ListType.Seasonal,
                    genre: seasonOption,
                    groupSeasons: false,
                    // Dashboard shelves are cached globally (every viewer
                    // hits the same row) so we can't honor a per-user
                    // showAdultContent toggle here. Default to the safer
                    // shape — explicit 18+ entries don't surface on the
                    // dashboard for anyone. Users who opt in can still
                    // find them via Discover / search.
                    hideAdult: true);

                top = list?.Take(15).ToList() ?? [];

                if (top.Count > 0)
                {
                    _dashboardCache.Set(cacheKey, top, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = PopularBySeasonCacheDuration,
                    });
                }
            }
            catch
            {
                top = [];
            }
        }

        // Cache stores the anilist:N-keyed list, shared across every viewer.
        // Per-call translation picks the id space — native (mal/kitsu/anilist)
        // for ungrouped users, imdb-first for grouped users — so the global
        // cache stays a single entry while each viewer's cards still land
        // on the id their /configure pref expects.
        return await _anilistFallback.TranslateMetaIdsAsync(top.Select(m => new Meta
        {
            id = m.id, name = m.name, poster = m.poster, type = m.type,
            score = m.score, episodes = m.episodes, year = m.year, format = m.format,
        }).ToList(), translateTo, groupSeasons);
    }

    [Route("/configure")]
    [Route("{config}/configure")]
    public Task<IActionResult> Configure(string config = null)
        => RenderConfigure("Stremio", config, externalLinksDefaultOnCreate: false);

    private async Task<IActionResult> RenderConfigure(string viewName, string config, bool externalLinksDefaultOnCreate)
    {
        var tokenData = await _tokenService.GetAccessTokenAsync(config);

        string configUid = null;
        long configRevision = 0;
        List<LinkedToken> linkedTokens = new();
        string encodedTokenData = null;
        string scrobbleToken = null;
        string plexUsername = null;
        List<StreamAddon> streamAddons = new();

        if (tokenData != null)
        {
            tokenData.expires_in = null;

            // Authenticated users get a v5 (UID-only) install URL: store the token JSON in
            // the config store and hand the JS just the UID. Idempotent — the same user
            // re-logging in keeps their UID. Anonymous users keep the inline (v3) flow
            // because their "token data" is just a 30-byte service preference and there's
            // no benefit to a DB row plus no stable identity to dedupe on.
            if (!tokenData.anonymousUser)
            {
                // One indexed lookup against the per-service identity columns finds the
                // row owning this user — whether the matched slot is the row's primary
                // or one of its linked secondaries. The flag distinguishes the two so
                // we can take the right write path:
                //
                //   - isPrimaryMatch: refresh primary tokens (rotated access_token /
                //     refresh_token from this login) and we're done.
                //   - !isPrimaryMatch: the user signed in via a service that's a linked
                //     secondary on this row — refresh the linked entry's tokens and
                //     switch the page over to the row's existing primary so the user
                //     lands on their prior setup instead of forking a duplicate.
                //   - miss: brand new identity, INSERT a fresh row via UpsertAsync.
                var (matchedUid, isPrimaryMatch) = await _configStore.FindUidByIdentityAsync(tokenData);
                if (!string.IsNullOrEmpty(matchedUid))
                {
                    configUid = matchedUid;
                    if (isPrimaryMatch)
                    {
                        await _configStore.UpdateByUserAsync(tokenData);
                    }
                    else
                    {
                        // Refresh the matching linked entry with the fresh tokens we
                        // just received. Clears any lingering needs-reauth state on the
                        // link the user literally just signed into.
                        await _configStore.SetLinkedTokenAsync(configUid, new LinkedToken
                        {
                            Service = tokenData.anime_service,
                            TokenData = tokenData,
                            NeedsReauth = false,
                        });
                        // Switch the page over to the row's existing primary so the
                        // downstream renders (linked accounts list, install URL,
                        // manifest payload, session token below) reflect the user's
                        // prior config rather than the just-logged-in linked secondary.
                        var primary = await _configStore.GetAsync(configUid);
                        if (primary != null) tokenData = primary;
                    }
                }
                else
                {
                    // First row for this identity. externalLinksDefaultOnCreate seeds the
                    // External services toggle: on for the /account entry, off for /configure
                    // (the Stremio path) — see the Account()/Configure() wrappers below.
                    configUid = await _configStore.UpsertAsync(tokenData, showExternalStreamsOnCreate: externalLinksDefaultOnCreate);
                }
                // Used as cache-busting bytes in the install URL — see Configure.cshtml's JS.
                configRevision = await _configStore.GetRevisionAsync(configUid);
                // Linked secondary accounts the multi-provider sync will fan writes out to.
                // The view renders a per-service Link / Unlink row from this list.
                linkedTokens = await _configStore.GetLinkedTokensAsync(configUid);
                // Lazily generated on first configure-page render. The webhook URL the user
                // pastes into Plex/Jellyfin/Emby is /api/v1/scrobble/{scrobbleToken}.
                scrobbleToken = await _configStore.EnsureScrobbleTokenAsync(configUid);
                plexUsername = await _configStore.GetPlexUsernameAsync(configUid);
                streamAddons = await _configStore.GetStreamAddonsAsync(configUid);

                // Hydrate the session from the config-URL-derived tokenData when the user
                // arrives via a v5 install URL (or any path that resolves identity from the
                // store rather than the cookie). Without this, post-login endpoints —
                // SetPrimary, SyncNow, LinkProvider, etc. — would all bail with "log in with
                // a primary provider first" because they read the session's AccessToken.
                // The config URL is already a per-user bearer token, so trusting it as a
                // login signal is consistent with the rest of the app.
                HttpContext.Session.SetString("AccessToken", SerializeObject(tokenData));
                // Persist the UID so the next request after a redeploy / PWA reopen can
                // rehydrate the session from the SQLite store via TokenService.
                _tokenService.SetPrimaryUidCookie(configUid);
            }
            else
            {
                if (tokenData.anime_service == AnimeService.Kitsu)
                {
                    tokenData.access_token = null;
                    tokenData.refresh_token = null;
                }
                encodedTokenData = CompressToUrlSafe(SerializeObject(tokenData, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            }
        }

        // Hydrate the toggle flags. The URL-config path covers Stremio's manifest deep-link
        // (v3/v5 bytes in the path); the UID fallback covers everything else — direct
        // visits to /configure, redirects after primary swap, login-completion landings — so
        // the page always reflects the user's saved state instead of falling back to defaults.
        Configuration configuration = null;
        if (!string.IsNullOrEmpty(config))
        {
            configuration = await ResolveConfigAsync(config, _configStore);
        }
        else if (!string.IsNullOrEmpty(configUid))
        {
            var (f1, f2, f3, _) = await _configStore.GetFlagsAsync(configUid);
            configuration = new Configuration();
            ApplyBinaryFlags(configuration, f1, f2, f3);
        }

        var anonymousUser = tokenData?.anonymousUser ?? false;

        // Anonymous users without a path-encoded config see a fresh page; pre-check the
        // catalogs that make sense without a connected list (Trending + Seasonal). Logged-in
        // users always have a configuration loaded (possibly all-zero) so this fallback only
        // really fires for the anonymous-fresh-install branch.
        configuration ??= anonymousUser
            ? new Configuration { showTrending = true, showSeasonal = true, discoverOnlySeasonal = true }
            : new Configuration();

        // /configure renders the Stremio addon page (catalogs / streams /
        // install URL + the shared Account header) because that's the URL
        // Stremio's "Configure" button appends to every addon manifest —
        // landing the user on the addon panel directly avoids a redirect
        // hop. The Account() and Advanced() actions below reuse this same
        // core, passing the account-focused / advanced view names (and the
        // External services default that goes with their entry point).
        // Enabled media-type set drives which settings render on the page — the
        // Preferences card's toggles are anime-only, so they (and the card) only
        // show when anime is among the user's selected modes.
        var enabledMediaTypes = await MediaTypePreference.ResolveEnabledAsync(HttpContext, configUid, _configStore);

        return View(viewName, new ConfigureViewModel
        {
            TokenData = encodedTokenData,
            ConfigUid = configUid,
            ConfigRevision = configRevision,
            AnimeService = tokenData?.anime_service ?? AnimeService.Kitsu,
            AnonymousUser = anonymousUser,
            EnabledMediaTypes = enabledMediaTypes,
            LinkedTokens = linkedTokens,
            ScrobbleToken = scrobbleToken,
            PlexUsername = plexUsername,
            StreamAddons = streamAddons,
            Configuration = configuration,
        });
    }

    /// <summary>
    /// Stremio addon configuration page — split off from /configure so users
    /// editing their list account don't have to scroll past the catalogs /
    /// streams / install URL panel that's only relevant to the Stremio
    /// integration. /stremio is preserved as a permanent redirect so any
    /// bookmark / external link from the previous URL still lands on the
    /// canonical /configure endpoint.
    /// </summary>
    [Route("/stremio")]
    public IActionResult Stremio() => RedirectPermanent("/configure");

    /// <summary>
    /// Account-focused settings page — Account picker, Linked Accounts,
    /// Preferences. Carries the content that used to live at /configure
    /// before Stremio's "Configure" URL convention claimed that path for
    /// the addon panel; lives off /account now so users can still reach
    /// the identity-focused view from the site nav.
    /// </summary>
    [Route("/account")]
    public Task<IActionResult> Account() => RenderConfigure("Configure", config: null, externalLinksDefaultOnCreate: true);

    /// <summary>
    /// Advanced settings page — Backups, Danger Zone, Home Server Sync.
    /// Reuses Configure's view-model so the JS handlers in
    /// _ConfigurePageScript bind the same way regardless of which view
    /// rendered them.
    /// </summary>
    [Route("/advanced")]
    public Task<IActionResult> Advanced() => RenderConfigure("Advanced", config: null, externalLinksDefaultOnCreate: false);

    /// <summary>
    /// Generates a fresh scrobble token for the given UID, invalidating any existing webhook
    /// URLs. Returns the new token so the JS can update the displayed URL without a reload.
    /// </summary>
    [HttpPost("Home/RotateScrobbleToken")]
    public async Task<JsonResult> RotateScrobbleToken()
    {
        var (uid, error) = await ResolveOwnUidOrErrorAsync();
        if (error != null) return error;

        var token = await _configStore.RotateScrobbleTokenAsync(uid);
        if (string.IsNullOrEmpty(token))
            return new JsonResult(new { success = false, error = "unknown uid" });

        return new JsonResult(new { success = true, token });
    }

    /// <summary>
    /// JSON projection of the signed-in user's top "Continue watching"
    /// items — slim shape (just the nine fields the dashboard's poster
    /// card actually renders) so the localStorage payload stays small.
    /// The dashboard JS hits this on first load, caches the data in
    /// localStorage for 10 minutes, and rebuilds the card DOM client-
    /// side on every render. Returns <c>items: []</c> when the user
    /// has no Watching entries so the JS can hide the section entirely.
    /// Manage-entry writes (modal save, +1 quick-action, delete) wipe
    /// the localStorage key from the client so the next dashboard
    /// render picks up the change.
    /// </summary>
    [HttpGet("Home/ContinueWatchingData")]
    public async Task<IActionResult> ContinueWatchingData()
    {
        var (token, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
        if (string.IsNullOrEmpty(uid) || token == null || token.anonymousUser)
        {
            return Unauthorized();
        }

        // Honor the user's general grouping pref the same way Library and
        // Discover do — when on, multi-cour franchises collapse into a single
        // IMDb-id card on the Continue Watching shelf, matching what the
        // user sees on the Stremio addon's Currently Watching catalog.
        // hideUnreleased keeps post-finale entries the user hasn't checked
        // off from sitting at the front of the shelf.
        var dashboardConfig = await GetConfigByUidAsync(uid, _configStore);
        var groupSeasons = dashboardConfig?.enableSeasonGrouping == true;
        var hideUnreleased = dashboardConfig?.hideUnreleasedFromWatching == true;
        var hideAdult = dashboardConfig?.showAdultContent != true;

        // Fan out across primary + every healthy linked secondary so the
        // shelf surfaces anime the user is watching on AniList / MAL / Kitsu
        // even when it isn't on their primary — same merge + dedup the
        // /library grid uses (MergedListService). Previously this fetched
        // the primary only, so a linked-only Currently-Watching entry went
        // missing from the dashboard.
        var metas = (await _mergedListService.GetMergedListAsync(
                token, uid, ListType.Current,
                genre: null, groupSeasons: groupSeasons,
                hideUnreleased: hideUnreleased, hideAdult: hideAdult))
            .Take(ContinueWatchingMaxItems)
            .ToList();

        // Render the same _PosterGrid partial every other shelf uses.
        // Keeping a single render path is the whole point of returning
        // HTML here instead of a JSON projection + a JS card builder
        // duplicating Views/Shared/_PosterGrid.cshtml's markup.
        return PartialView("_PosterGrid", new PosterGridViewModel
        {
            Items = metas,
            ConfigUid = uid,
            Variant = "scroll",
        });
    }

    // ── Client-loaded dashboard shelves ──────────────────────────────────
    // The discovery shelves and the "This Season" stat strip are fetched by
    // the inline loaders in Index.cshtml once the page paints, so the dashboard
    // render never blocks on AniList and each shelf loads independently / in
    // parallel. Each poster shelf returns the same _PosterGrid scroll partial
    // the rest of the app uses; an empty body tells the client to hide that
    // shelf. The underlying data is still 24h / until-midnight cached inside
    // AnilistFallback + GetPopularBySeasonAsync, so these endpoints are cheap
    // on repeat hits.

    [HttpGet("Home/NewEpisodesTodayData")]
    public async Task<IActionResult> NewEpisodesTodayData()
    {
        var (primaryService, uid, groupSeasons) = await ResolveDashboardShelfContextAsync();
        var metas = await _anilistFallback.GetNewEpisodesTodayAsync(primaryService, ReadTimezoneOffsetMinutes(), groupSeasons);
        return ShelfPartial(metas, uid);
    }

    [HttpGet("Home/TrendingAnimeData")]
    public async Task<IActionResult> TrendingAnimeData()
    {
        var (primaryService, uid, groupSeasons) = await ResolveDashboardShelfContextAsync();
        var metas = await GetCatalogShelfAsync(ListType.Trending_Desc, primaryService, groupSeasons);
        return ShelfPartial(metas, uid);
    }

    [HttpGet("Home/PopularAnimeData")]
    public async Task<IActionResult> PopularAnimeData()
    {
        var (primaryService, uid, groupSeasons) = await ResolveDashboardShelfContextAsync();
        var metas = await GetCatalogShelfAsync(ListType.Popularity_Desc, primaryService, groupSeasons);
        return ShelfPartial(metas, uid);
    }

    [HttpGet("Home/MostAnticipatedData")]
    public async Task<IActionResult> MostAnticipatedData()
    {
        var (primaryService, uid, groupSeasons) = await ResolveDashboardShelfContextAsync();
        var metas = await GetPopularBySeasonAsync(SeasonNext, primaryService, groupSeasons);
        return ShelfPartial(metas, uid);
    }

    // Shared _PosterGrid scroll-row partial for every client-loaded shelf.
    // Empty Items + no EmptyMessage renders an empty body, which the shelf
    // loader reads as "hide this section".
    private PartialViewResult ShelfPartial(List<Meta> metas, string uid) =>
        PartialView("_PosterGrid", new PosterGridViewModel
        {
            Items = metas ?? [],
            ConfigUid = uid,
            Variant = "scroll",
        });

    // ── Video-mode dashboard shelves (movies / series) ───────────────────
    // Mirror the anime shelf endpoints but source from Cinemeta + Trakt and
    // emit VideoLinks cards (so clicks carry ?type=). Same scroll partial +
    // generic [data-shelf] loader the anime shelves use; an empty body hides
    // the section. Trakt items carry no posters, so they're hydrated through
    // Cinemeta in parallel (preserving Trakt's ranked order).

    // Per-shelf cap — each Trakt item costs a Cinemeta meta lookup for its
    // poster, so keep the dashboard fan-out bounded (matches the anime shelves'
    // top-15 slice).
    private const int VideoShelfSize = 15;

    private PartialViewResult VideoShelfPartial(List<Meta> metas, string uid) =>
        PartialView("_PosterGrid", new PosterGridViewModel
        {
            Items = metas ?? [],
            ConfigUid = uid,
            Variant = "scroll",
            VideoLinks = true,
        });

    // Trending / Most Popular / Most Anticipated for the video dashboard.
    // Public feeds (no user identity required); "popular" is Cinemeta's top
    // catalog, the others are Trakt's ranked discovery feeds.
    [HttpGet("Home/VideoShelfData")]
    public async Task<IActionResult> VideoShelfData(string type, string mode)
    {
        if (type != "movie" && type != "series") type = "movie";
        var uid = await ResolveCurrentUidAsync();

        List<Meta> metas;
        if (mode == "popular")
        {
            // Trakt's popular list (hydrated via Cinemeta); fall back to Cinemeta's
            // own catalog when Trakt isn't configured.
            if (_trakt.IsConfigured)
            {
                var items = await _trakt.GetDiscoveryAsync(uid, type, "popular", genre: null, page: 1, limit: VideoShelfSize);
                metas = items.ToVideoMetas();
            }
            else
            {
                metas = (await _cinemeta.GetVideoCatalogAsync(type)).Take(VideoShelfSize).ToList();
            }
        }
        else if (mode is "trending" or "anticipated")
        {
            if (!_trakt.IsConfigured) return VideoShelfPartial([], uid);
            var items = await _trakt.GetDiscoveryAsync(uid, type, mode, genre: null, page: 1, limit: VideoShelfSize);
            metas = items.ToVideoMetas();
        }
        else
        {
            metas = [];
        }
        return VideoShelfPartial(metas, uid);
    }

    // Video "Continue watching" — Trakt's in-progress playback (paused movies +
    // episodes), hydrated via Cinemeta. Trakt-connected users only.
    [HttpGet("Home/VideoContinueWatchingData")]
    public async Task<IActionResult> VideoContinueWatchingData(string type = null)
    {
        var uid = await ResolveCurrentUidAsync();
        if (string.IsNullOrEmpty(uid)) return Unauthorized();
        IEnumerable<TraktListItem> playback = await _trakt.GetPlaybackAsync(uid);
        // The combined dashboard renders a per-type Continue Watching shelf, so
        // filter the mixed playback feed (movies + episodes) to the requested
        // type when one is given.
        if (type is "movie" or "series")
            playback = playback.Where(i => i.Type == type);
        var metas = playback.Take(ContinueWatchingMaxItems).ToList().ToVideoMetas();
        return VideoShelfPartial(metas, uid);
    }

    // Aggregate Trakt stats for the video dashboard's "Your stats" strip.
    // Returns success=false (and the client keeps placeholders / hides the
    // panel) when Trakt isn't connected or the upstream blips.
    [HttpGet("Home/TraktStatsData")]
    public async Task<JsonResult> TraktStatsData()
    {
        var uid = await ResolveCurrentUidAsync();
        if (string.IsNullOrEmpty(uid))
        {
            Response.StatusCode = 401;
            return new JsonResult(new { success = false });
        }
        var stats = await _trakt.GetUserStatsAsync(uid);
        return stats == null
            ? new JsonResult(new { success = false })
            : new JsonResult(new { success = true, stats });
    }

    // Resolves the per-viewer context every dashboard shelf needs: the
    // primary service (drives the id space card clicks land on), the resolved
    // UID (per-card Manage Entry hand-off), and the season-grouping pref.
    // Anonymous / not-logged-in viewers get (session-default-or-AniList, null,
    // false) — the shelves still render, just without user-list context.
    private async Task<(AnimeService primaryService, string uid, bool groupSeasons)> ResolveDashboardShelfContextAsync()
    {
        var primaryService = AnimeService.Anilist;
        string uid = null;
        var groupSeasons = false;

        var sessionStr = HttpContext.Session.GetString("AccessToken");
        if (!string.IsNullOrEmpty(sessionStr))
        {
            var tokenData = DeserializeObject<TokenData>(sessionStr);
            if (tokenData != null)
            {
                primaryService = tokenData.anime_service;
                if (!tokenData.anonymousUser)
                {
                    var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                    uid = resolved;
                    if (!string.IsNullOrEmpty(uid))
                    {
                        var config = await GetConfigByUidAsync(uid, _configStore);
                        groupSeasons = config?.enableSeasonGrouping == true;
                    }
                }
            }
        }
        return (primaryService, uid, groupSeasons);
    }

    // Reads + clamps the timezone-offset cookie the layout stamps (JS
    // getTimezoneOffset convention: minutes west of UTC). Defaults to 0 (UTC)
    // when the cookie isn't present yet (brand-new visitor's first request).
    private int ReadTimezoneOffsetMinutes()
    {
        var tzCookie = Request.Cookies["anisync_tz"];
        if (!string.IsNullOrEmpty(tzCookie) && int.TryParse(tzCookie, out var parsed))
            return Math.Clamp(parsed, -840, 720);
        return 0;
    }

    /// <summary>
    /// Returns the signed-in user's AniList "Your stats" panel data
    /// (watching count, completed count, hours watched, mean score) so the
    /// dashboard JS can render the cells without a server round-trip
    /// blocking the SSR. Cached client-side in localStorage for 24 h;
    /// the "refresh your stats" link in the stats-info popover wipes that
    /// key and re-hits this endpoint. Returns 200 with stats=null when the
    /// user has no AniList token (primary or linked) so the JS can keep
    /// its placeholders without retrying.
    /// </summary>
    [HttpGet("Home/AnilistStats")]
    public async Task<JsonResult> AnilistStats()
    {
        var (token, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
        if (string.IsNullOrEmpty(uid))
        {
            Response.StatusCode = 401;
            return new JsonResult(new { success = false, error = "Sign in first." });
        }

        TokenData anilistToken = null;
        if (token.anime_service == AnimeService.Anilist)
        {
            anilistToken = token;
        }
        else
        {
            var linked = await _configStore.GetLinkedTokensAsync(uid);
            foreach (var lt in linked)
            {
                if (lt.NeedsReauth || lt.TokenData == null || lt.TokenData.anonymousUser) continue;
                if (lt.Service != AnimeService.Anilist) continue;
                anilistToken = lt.TokenData;
                break;
            }
        }

        if (anilistToken == null)
        {
            return new JsonResult(new { success = true, stats = (AnilistUserStats?)null });
        }

        var stats = await _anilistService.GetUserStatsAsync(anilistToken);
        return new JsonResult(new { success = true, stats });
    }

    /// <summary>
    /// Stores the optional Plex Home username filter for shared servers. Empty / whitespace
    /// clears the filter (events from any username will scrobble).
    /// </summary>
    [HttpPost("Home/SetPlexUsername")]
    public async Task<JsonResult> SetPlexUsername([FromBody] PlexUsernameRequest request)
    {
        var (uid, error) = await ResolveOwnUidOrErrorAsync();
        if (error != null) return error;

        await _configStore.SetPlexUsernameAsync(uid, request?.username);
        return new JsonResult(new { success = true });
    }

    /// <summary>
    /// Adds a Stremio stream addon to the user's list. Server fetches the
    /// manifest URL once to validate it advertises stream support and to
    /// pull the display name, then persists the (url, name) pair. Returns
    /// the stored entry so the client can append it to the rendered list
    /// without a separate refresh round-trip.
    /// </summary>
    [HttpPost("Home/AddStreamAddon")]
    public async Task<JsonResult> AddStreamAddon([FromBody] StreamAddonRequest request)
    {
        var (uid, error) = await ResolveOwnUidOrErrorAsync();
        if (error != null) return error;
        var manifestUrl = (request?.manifestUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(manifestUrl))
            return new JsonResult(new { success = false, error = "manifest URL required" });

        var addon = await _addonStreamService.FetchManifestAsync(manifestUrl);
        if (addon == null)
        {
            return new JsonResult(new { success = false,
                error = "couldn't fetch a Stremio stream-addon manifest at that URL" });
        }

        // First-addon transition: once the user has any stream addon, AniSync can serve real
        // streams, so the external streaming-site links default off — but only on the 0→1 add,
        // so a user who later re-enables External services keeps that choice (see decision note).
        var hadAddons = (await _configStore.GetStreamAddonsAsync(uid)).Count > 0;
        var added = await _configStore.AddStreamAddonAsync(uid, addon);
        if (added && !hadAddons)
            await _configStore.ClearShowExternalStreamsAsync(uid);
        // The matching stream cache lives in the user's browser
        // (localStorage anisync.streams.{uid}.*) and is wiped client-
        // side by the same handler that hit this endpoint — see
        // _ConfigurePageScript.cshtml.
        return new JsonResult(new { success = true, added, addon });
    }

    /// <summary>
    /// Removes a Stremio stream addon by manifest URL. Returns whether
    /// anything was removed; UIs that already optimistically removed the
    /// row can ignore the result.
    /// </summary>
    [HttpPost("Home/RemoveStreamAddon")]
    public async Task<JsonResult> RemoveStreamAddon([FromBody] StreamAddonRequest request)
    {
        var (uid, error) = await ResolveOwnUidOrErrorAsync();
        if (error != null) return error;
        var manifestUrl = (request?.manifestUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(manifestUrl))
            return new JsonResult(new { success = false, error = "manifest URL required" });

        var removed = await _configStore.RemoveStreamAddonAsync(uid, manifestUrl);
        // Client-side localStorage cache wipe happens in
        // _ConfigurePageScript.cshtml's remove handler.
        return new JsonResult(new { success = true, removed });
    }

    /// <summary>
    /// Reorders the user's stream-addon list to match the supplied URL
    /// order. Backs the drag-and-drop / up-down reorder buttons on the
    /// /advanced Streams card.
    /// </summary>
    [HttpPost("Home/ReorderStreamAddons")]
    public async Task<JsonResult> ReorderStreamAddons([FromBody] ReorderStreamAddonsRequest request)
    {
        var (uid, error) = await ResolveOwnUidOrErrorAsync();
        if (error != null) return error;
        if (request?.urls == null || request.urls.Count == 0)
            return new JsonResult(new { success = false, error = "urls required" });

        var changed = await _configStore.ReorderStreamAddonsAsync(uid, request.urls);
        // Client-side localStorage cache wipe happens in
        // _ConfigurePageScript.cshtml's save handler.
        return new JsonResult(new { success = true, changed });
    }

    /// <summary>
    /// One-click debrid setup: given a debrid provider + API key and a set
    /// of catalog addon ids, builds each addon's manifest URL from
    /// <see cref="StreamAddonCatalog"/>, validates it the same way
    /// <see cref="AddStreamAddon"/> does (fetch the manifest, confirm it
    /// advertises stream support, derive the display name), and persists
    /// the ones that check out. Returns the added entries so the client can
    /// render rows without a refresh, plus per-addon skip reasons so it can
    /// tell the user what couldn't be auto-configured (already added, or an
    /// addon whose upstream config format drifted). The API key is used
    /// only to mint the URLs — it lives inside the stored manifest URL and
    /// is never logged here.
    /// </summary>
    [HttpPost("Home/AddDebridAddons")]
    public async Task<JsonResult> AddDebridAddons([FromBody] DebridAddonsRequest request)
    {
        var (uid, error) = await ResolveOwnUidOrErrorAsync();
        if (error != null) return error;

        var provider = StreamAddonCatalog.FindProvider(request?.provider);
        if (provider == null)
            return new JsonResult(new { success = false, error = "unknown debrid provider" });
        var apiKey = (request?.apiKey ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(apiKey))
            return new JsonResult(new { success = false, error = "API key required" });

        // No addon subset pinned by the client → set up the whole catalog.
        var addonIds = (request.addons != null && request.addons.Count > 0)
            ? request.addons
            : StreamAddonCatalog.Addons.Select(a => a.Id).ToList();

        // Captured before the loop so the external-links clear below keys off whether the user
        // already had addons — debrid setup adds several at once, but it's still a single 0→1
        // transition for the External services default.
        var hadAddons = (await _configStore.GetStreamAddonsAsync(uid)).Count > 0;

        var added = new List<StreamAddon>();
        var skipped = new List<object>();
        foreach (var addonId in addonIds)
        {
            if (!StreamAddonCatalog.IsKnownAddon(addonId))
            {
                skipped.Add(new { addon = addonId, reason = "unknown addon" });
                continue;
            }

            // One addon can map to several manifest URLs — Comet runs as two
            // interchangeable public instances, so episode lookups fan out
            // across the pair. MediaFusion is the odd one out: its config is
            // encrypted with the addon's own server-side key, so we can't
            // build its URL offline — we POST the config to MediaFusion's
            // encrypt endpoint and wrap the returned token. Everything else
            // is pure offline string-building. Each resulting URL is then
            // validated + added independently.
            IReadOnlyList<string> manifestUrls;
            if (string.Equals(addonId, "mediafusion", StringComparison.OrdinalIgnoreCase))
            {
                var configJson = StreamAddonCatalog.BuildMediaFusionConfigJson(provider, apiKey);
                var token = configJson == null
                    ? null
                    : await _addonStreamService.EncryptConfigAsync(
                        StreamAddonCatalog.MediaFusionEncryptUrl, configJson);
                manifestUrls = string.IsNullOrEmpty(token)
                    ? Array.Empty<string>()
                    : new[] { $"{StreamAddonCatalog.MediaFusionHost}/{token}/manifest.json" };
            }
            else
            {
                manifestUrls = StreamAddonCatalog.BuildManifestUrls(addonId, provider, apiKey);
            }

            if (manifestUrls.Count == 0)
            {
                skipped.Add(new { addon = addonId, reason = "couldn't build a manifest URL" });
                continue;
            }

            foreach (var manifestUrl in manifestUrls)
            {
                // Same validation gate as the manual Add path: a built URL
                // that doesn't resolve to a stream-capable manifest (provider
                // down, bad key, drifted config format) is skipped, not
                // stored.
                var addon = await _addonStreamService.FetchManifestAsync(manifestUrl);
                if (addon == null)
                {
                    skipped.Add(new { addon = addonId, reason = "couldn't validate — check your key, or add it manually" });
                    continue;
                }

                var wasAdded = await _configStore.AddStreamAddonAsync(uid, addon);
                if (wasAdded) added.Add(addon);
                else skipped.Add(new { addon = addonId, reason = "already added" });
            }
        }

        // First-addon transition (see AddStreamAddon): default External services off once the
        // user goes from zero addons to having some. No-op if they already had addons, or if the
        // toggle was already off / they later turned it back on.
        if (added.Count > 0 && !hadAddons)
            await _configStore.ClearShowExternalStreamsAsync(uid);

        // Matching browser stream cache (anisync.streams.{uid}.*) is wiped
        // client-side by the same handler that hit this endpoint — see
        // _ConfigurePageScript.cshtml.
        return new JsonResult(new { success = true, added, skipped });
    }

    /// <summary>
    /// Persists the toggle bits to the config store for the given UID. Auto-called by the
    /// Install button on the configure page so the manifest the addon serves immediately
    /// reflects the user's current toggle state. Bumps the revision so the install URL
    /// changes (Stremio refuses to refetch a URL it already has cached).
    /// </summary>
    [HttpPost("Home/SaveConfig")]
    public async Task<JsonResult> SaveConfig([FromBody] SaveConfigRequest request)
    {
        var (uid, error) = await ResolveOwnUidOrErrorAsync();
        if (error != null) return error;
        if (request == null)
            return new JsonResult(new { success = false, error = "missing body" });

        var revision = await _configStore.SetFlagsAsync(uid, request.flags1, request.flags2, request.flags3);
        // Non-flag web preference (its own column) — persist when the client sent
        // a value; a null leaves the stored setting untouched.
        if (request.hideCompletedDiscover.HasValue)
            await _configStore.SetHideCompletedFromDiscoverAsync(uid, request.hideCompletedDiscover.Value);
        return new JsonResult(new { success = true, revision });
    }

    /// <summary>
    /// Sets the ACTIVE media-type cookie (anime / movie / series) then reloads
    /// the page the Discover / Library toggle was triggered from. Cookie-only —
    /// there is no DB setting; the chooser modal owns the enabled set, and this
    /// just switches which enabled mode the current surface renders. Form POST
    /// (antiforgery-protected) so the toggle works without JS.
    /// </summary>
    [HttpPost("Home/SetMediaType")]
    public IActionResult SetMediaType([FromForm] int mediaType, [FromForm] string returnUrl = null)
    {
        if (Enum.IsDefined(typeof(MetaType), mediaType))
        {
            Response.Cookies.Append(MediaTypePreference.CookieName, ((MetaType)mediaType).ToString(), new CookieOptions
            {
                Path = "/",
                MaxAge = TimeSpan.FromDays(365),
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
            });
        }

        // An AJAX caller (the modal) reloads itself, so hand it a bare 204
        // rather than a redirect it would pointlessly follow.
        if (string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            return NoContent();

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Account");
    }

    /// <summary>
    /// Persists the enabled media-type set (the chooser modal's multi-select) to
    /// the account so it follows the user across devices. AJAX-only — the modal
    /// also writes localStorage + the cookie and reloads itself. No-op (still 204)
    /// for anonymous callers, who only have the cookie.
    /// </summary>
    [HttpPost("Home/SetEnabledMediaTypes")]
    public async Task<IActionResult> SetEnabledMediaTypes([FromForm] string enabled)
    {
        var uid = await ResolveCurrentUidAsync();
        if (!string.IsNullOrEmpty(uid))
            await _configStore.SetEnabledMediaTypesAsync(uid, NormalizeModes(enabled));
        return NoContent();
    }

    /// <summary>
    /// Persists the dashboard layout (section order + visibility JSON) to the
    /// account. AJAX-only; the layout also lives in localStorage. Length-capped
    /// so a hostile client can't store an arbitrarily large blob.
    /// </summary>
    [HttpPost("Home/SetDashboardLayout")]
    public async Task<IActionResult> SetDashboardLayout([FromForm] string layout)
    {
        var uid = await ResolveCurrentUidAsync();
        if (!string.IsNullOrEmpty(uid))
        {
            var json = string.IsNullOrWhiteSpace(layout) || layout.Length > 4000 ? null : layout;
            await _configStore.SetDashboardLayoutAsync(uid, json);
        }
        return NoContent();
    }

    // Sanitises a client-supplied mode csv to the known modes, de-duped + in a
    // stable order; null when nothing valid remains.
    private static string NormalizeModes(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var order = new[] { "anime", "movie", "series" };
        var set = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(order.Contains)
            .ToHashSet();
        return set.Count == 0 ? null : string.Join(",", order.Where(set.Contains));
    }

    /// <summary>
    /// Streams the current configuration as a downloadable JSON file. Backup contains
    /// everything needed to restore the user on another browser/device: their token data
    /// (so they don't have to log in again) plus the toggle flags.
    /// </summary>
    [HttpGet("Home/ExportConfig")]
    public async Task<IActionResult> ExportConfig()
    {
        var tokenData = await _tokenService.GetAccessTokenAsync();
        if (tokenData == null || tokenData.anonymousUser)
            return BadRequest("No configuration to export.");

        var uid = await _configStore.UpsertAsync(tokenData);
        var (f1, f2, f3, _) = await _configStore.GetFlagsAsync(uid);

        var backup = new ConfigBackup
        {
            version = 1,
            service = tokenData.anime_service.ToString(),
            tokenData = tokenData,
            flags = new BackupFlags { flags1 = f1, flags2 = f2, flags3 = f3 },
            hideCompletedDiscover = await _configStore.GetHideCompletedFromDiscoverAsync(uid),
        };

        var json = SerializeObject(backup, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        var fileName = $"anisync-config-{tokenData.anime_service.ToString().ToLower()}-{DateTime.UtcNow:yyyyMMdd}.json";
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", fileName);
    }

    /// <summary>
    /// Restores a configuration from an exported backup file. Replaces the current session
    /// (if any) and writes the backup's tokens + flags into the config store, returning the
    /// new UID + revision so the JS can rebuild the install URL.
    /// </summary>
    [HttpPost("Home/ImportConfig")]
    public async Task<JsonResult> ImportConfig([FromBody] ConfigBackup backup)
    {
        if (backup?.tokenData == null)
            return new JsonResult(new { success = false, error = "Backup file is missing tokenData." });

        // Re-establish the session so the configure page recognises the user without
        // forcing them through OAuth/login again.
        HttpContext.Session.SetString("AccessToken", SerializeObject(backup.tokenData));

        var uid = await _configStore.UpsertAsync(backup.tokenData);
        // Persist UID for session rehydration after restart / PWA reopen.
        _tokenService.SetPrimaryUidCookie(uid);
        if (backup.flags != null)
            await _configStore.SetFlagsAsync(uid, backup.flags.flags1, backup.flags.flags2, backup.flags.flags3);
        if (backup.hideCompletedDiscover.HasValue)
            await _configStore.SetHideCompletedFromDiscoverAsync(uid, backup.hideCompletedDiscover.Value);

        return new JsonResult(new { success = true });
    }

    /// <summary>
    /// Resets the toggle bits to all-zero, keeping the user logged in. Bumps the revision
    /// so Stremio sees a different install URL.
    /// </summary>
    [HttpPost("Home/ResetConfig")]
    public async Task<JsonResult> ResetConfig()
    {
        var (uid, error) = await ResolveOwnUidOrErrorAsync();
        if (error != null) return error;

        var revision = await _configStore.SetFlagsAsync(uid, 0, 0, 0);
        return new JsonResult(new { success = true, revision });
    }

    /// <summary>
    /// Removes the configuration row from the store and ends the session. After this the
    /// user is fully signed out and any old install URLs they had become dead links.
    /// </summary>
    [HttpPost("Home/DeleteConfig")]
    public async Task<JsonResult> DeleteConfig()
    {
        var uid = await ResolveCurrentUidAsync();
        if (!string.IsNullOrEmpty(uid))
            await _configStore.DeleteAsync(uid);

        await _tokenService.RemoveCachedUser();
        HttpContext.Session.Clear();
        return new JsonResult(new { success = true });
    }

    /// <summary>
    /// Resolves the current session's install UID the same way the configure page does —
    /// from the authenticated primary identity, never from a client-supplied value — so
    /// the destructive UID actions below can only ever target the caller's own row.
    /// Returns null for anonymous or signed-out callers.
    /// </summary>
    private async Task<string> ResolveCurrentUidAsync()
    {
        var sessionStr = HttpContext.Session.GetString("AccessToken");
        if (string.IsNullOrEmpty(sessionStr)) return null;
        var token = DeserializeObject<TokenData>(sessionStr);
        if (token == null || token.anonymousUser) return null;
        var (uid, _) = await _configStore.FindUidByIdentityAsync(token);
        return uid;
    }

    /// <summary>
    /// Session-derived UID for a state-changing Home/* endpoint, or a JSON error when the
    /// caller isn't a signed-in non-anonymous user. Every mutation keys off this instead of
    /// a body-supplied UID: the UID travels in the Stremio install URL (and screenshots,
    /// logs, support threads), so trusting a body value would let anyone who has merely seen
    /// a UID flip toggles, rewrite settings, or harvest the scrobble token of that row.
    /// </summary>
    private async Task<(string uid, JsonResult error)> ResolveOwnUidOrErrorAsync()
    {
        var uid = await ResolveCurrentUidAsync();
        if (string.IsNullOrEmpty(uid))
            return (null, new JsonResult(new { success = false, error = "not signed in" }));
        return (uid, null);
    }

    /// <summary>
    /// Rotates the install UID for the signed-in user, keeping all of their data. Every
    /// old Stremio install URL and X-AniSync-Config header that carried the previous UID
    /// stops resolving — the recovery path for a leaked UID — while this browser stays
    /// signed in (the persistent UID cookie is rewritten to the new value). The client
    /// reloads so the install URL and the rest of the UID-derived UI re-render. The target
    /// UID comes from the session, so a bare leaked UID can't drive this on its own.
    /// </summary>
    [HttpPost("Home/RegenerateUid")]
    public async Task<JsonResult> RegenerateUid()
    {
        var uid = await ResolveCurrentUidAsync();
        if (string.IsNullOrEmpty(uid))
            return new JsonResult(new { success = false, error = "not signed in" });

        var newUid = await _configStore.RotateUidAsync(uid);
        if (string.IsNullOrEmpty(newUid))
            return new JsonResult(new { success = false, error = "unknown uid" });

        // The session AccessToken carries no UID, so only the rehydration cookie needs
        // rewriting to keep this browser resolving against the freshly-rotated row.
        _tokenService.SetPrimaryUidCookie(newUid);
        return new JsonResult(new { success = true, uid = newUid });
    }

    /// <summary>
    /// Rotates the UID (invalidating every existing install URL, header, and persisted
    /// cookie everywhere) and then clears this browser's session + cookie too, signing the
    /// user out on all devices at once. The row's data is preserved under the new UID, so
    /// signing back in with the same provider lands on the same configuration.
    /// </summary>
    [HttpPost("Home/SignOutEverywhere")]
    public async Task<JsonResult> SignOutEverywhere()
    {
        var uid = await ResolveCurrentUidAsync();
        if (string.IsNullOrEmpty(uid))
            return new JsonResult(new { success = false, error = "not signed in" });

        await _configStore.RotateUidAsync(uid);
        await _tokenService.RemoveCachedUser();   // drops in-memory token cache + session + cookie
        HttpContext.Session.Clear();
        return new JsonResult(new { success = true });
    }

}

public class SaveConfigRequest
{
    public string uid { get; set; }
    public byte flags1 { get; set; }
    public byte flags2 { get; set; }
    public byte flags3 { get; set; }
    // Non-flag web preference persisted to its own store column. Nullable so a
    // client that doesn't render the Preferences card (or omits the field)
    // leaves the stored value untouched rather than forcing it off.
    public bool? hideCompletedDiscover { get; set; }
}

public class PlexUsernameRequest
{
    public string uid { get; set; }
    public string username { get; set; }
}

public class StreamAddonRequest
{
    public string uid { get; set; }
    // Full Stremio addon manifest URL ending in /manifest.json. For add,
    // the server fetches it once to validate and derive the display name;
    // for remove, it's the lookup key (string-compared, case-insensitive).
    public string manifestUrl { get; set; }
}

public class ReorderStreamAddonsRequest
{
    public string uid { get; set; }
    // URL list in the new desired order. Match is case-insensitive
    // against StreamAddon.Url; unknown URLs are silently skipped and
    // any addon the list omits stays at its current relative position
    // (defensive against stale clients losing rows).
    public List<string> urls { get; set; }
}

public class DebridAddonsRequest
{
    public string uid { get; set; }
    // Catalog debrid provider id (StreamAddonCatalog.Providers), e.g.
    // "realdebrid". Resolved server-side; unknown ids are rejected.
    public string provider { get; set; }
    // The user's debrid API key. Flows into each built manifest URL and is
    // never logged on its own.
    public string apiKey { get; set; }
    // Catalog addon ids to set up (StreamAddonCatalog.Addons), e.g.
    // ["torrentio", "comet"]. Null / empty means "all catalog addons".
    public List<string> addons { get; set; }
}

public class ConfigBackup
{
    public int version { get; set; }
    public string service { get; set; }
    public TokenData tokenData { get; set; }
    public BackupFlags flags { get; set; }
    // Non-flag web preference. Nullable so older backups (without the field)
    // restore cleanly, leaving the default (off).
    public bool? hideCompletedDiscover { get; set; }
}

public class BackupFlags
{
    public byte flags1 { get; set; }
    public byte flags2 { get; set; }
    public byte flags3 { get; set; }
}
