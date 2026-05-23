using AnimeList.Models;
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
    private readonly IKitsuService _kitsuService;
    private readonly IMalService _malService;
    private readonly IAnilistFallback _anilistFallback;
    private readonly IAddonStreamService _addonStreamService;
    private readonly IUserListCache _listCache;
    private readonly IMemoryCache _dashboardCache;

    // Day-stale rankings are indistinguishable from live ones for the
    // popularity shelves — same TTL the seasonal-stats cache uses inside
    // AnilistFallback. Kept private here because only the dashboard's
    // popular-by-season helper needs it.
    private static readonly TimeSpan PopularBySeasonCacheDuration = TimeSpan.FromHours(24);

    public HomeController(
        ITokenService tokenService,
        IConfigStore configStore,
        IAnilistService anilistService,
        IKitsuService kitsuService,
        IMalService malService,
        IAnilistFallback anilistFallback,
        IAddonStreamService addonStreamService,
        IUserListCache listCache,
        IMemoryCache dashboardCache)
    {
        _tokenService = tokenService;
        _configStore = configStore;
        _anilistService = anilistService;
        _kitsuService = kitsuService;
        _malService = malService;
        _anilistFallback = anilistFallback;
        _addonStreamService = addonStreamService;
        _listCache = listCache;
        _dashboardCache = dashboardCache;
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
    /// ServerError. The original status code is restored on the response so
    /// crawlers / share-link previewers see the real code even though we're
    /// returning a full HTML body.
    /// </summary>
    [Route("/error/{statusCode:int?}")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult ErrorPage(int? statusCode)
    {
        var code = statusCode ?? 500;
        Response.StatusCode = code;
        return View(code == 404 ? "NotFound" : "ServerError");
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

        // Seasonal stats + popularity shelves apply to every visitor
        // (anonymous + logged-in) since they describe the whole AniList
        // catalog, not the user's list. All three calls run in parallel —
        // the popularity shelves hit a different sort/perPage of the same
        // GraphQL endpoint as the stats query and are independently 24h-
        // cached by AnilistFallback. Failures swallow into empty results
        // and the view hides the corresponding shelves.
        var seasonStatsTask = _anilistFallback.GetSeasonStatsAsync();
        // Re-uses the same _anilistService.GetAnimeListAsync(ListType.Seasonal,
        // genre: "This Season" / "Next Season") path the public Discover page
        // and the /api/v1/discover endpoint serve — same upstream query, same
        // POPULARITY_DESC default sort — wrapped in a 24h IMemoryCache here
        // so the dashboard doesn't pay an AniList round-trip on every visit.
        // Translate dashboard-shelf ids into the viewer's primary id space
        // (mal:N / kitsu:N when mapped, anilist:N as fallback) so card
        // clicks land on the user's primary's detail-page id rather than
        // bouncing through a cross-service id resolve. Anonymous and
        // AniList-primary viewers get an effective no-op translation.
        var primaryService = tokenData?.anime_service ?? AnimeService.Anilist;
        // Per-user grouping pref — read once and threaded through every
        // dashboard shelf's translate step. The shelf data itself stays
        // globally cached (anilist:N ids); the per-request transform picks
        // between native-id and imdb-id resolvers so each viewer sees the
        // ids their toggle expects without per-user cache fanout.
        var dashboardGroupSeasons = false;
        if (!string.IsNullOrEmpty(uid))
        {
            var dashboardConfig = await GetConfigByUidAsync(uid, _configStore);
            dashboardGroupSeasons = dashboardConfig?.enableSeasonGrouping == true;
        }
        var popularThisSeasonTask = GetPopularBySeasonAsync(SeasonCurrent, primaryService, dashboardGroupSeasons);
        var mostAnticipatedTask = GetPopularBySeasonAsync(SeasonNext, primaryService, dashboardGroupSeasons);
        // Read the timezone-offset cookie stamped by the layout's inline
        // script. Defaults to 0 (UTC) when the cookie isn't present yet
        // (brand-new visitor's first request — the cookie is set during
        // that render, so the next navigation picks up the right value).
        var tzOffsetMinutes = 0;
        var tzCookie = Request.Cookies["anisync_tz"];
        if (!string.IsNullOrEmpty(tzCookie) && int.TryParse(tzCookie, out var parsed))
        {
            // JS getTimezoneOffset ranges from -840 (UTC+14) to +720
            // (UTC-12). Clamp defensively so a malformed cookie can't
            // make the cache key absurd.
            tzOffsetMinutes = Math.Clamp(parsed, -840, 720);
        }
        var newEpisodesTodayTask = _anilistFallback.GetNewEpisodesTodayAsync(primaryService, tzOffsetMinutes, dashboardGroupSeasons);
        await Task.WhenAll(seasonStatsTask, popularThisSeasonTask, mostAnticipatedTask, newEpisodesTodayTask);

        var (seasonAiring, seasonNew, seasonTotal) = await seasonStatsTask;

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

        return View(new DashboardViewModel
        {
            TokenData = tokenData,
            ConfigUid = uid,
            LinkedServices = linkedServiceNames,
            HasStats = hasStats,
            ContributingServices = contributingNames,
            SeasonCurrentlyAiring = seasonAiring,
            SeasonNewThis = seasonNew,
            SeasonTotal = seasonTotal,
            PopularThisSeason = popularThisSeasonTask.Result,
            MostAnticipated = mostAnticipatedTask.Result,
            NewEpisodesToday = newEpisodesTodayTask.Result,
        });
    }

    // Per-service GetAnimeListAsync dispatch — the same shape Library / Discover /
    // Catalog all use, factored here so Index can fetch two list types in parallel
    // without three nested switch expressions. groupSeasons is plumbed through so
    // the dashboard's Continue Watching / stats slice respects the user's
    // "Group anime seasons" toggle the same way the addon catalog does.
    private async Task<List<Meta>> FetchListAsync(TokenData tokenData, ListType listType, bool groupSeasons = true, bool hideUnreleased = false, bool hideAdult = false)
    {
        var metas = tokenData.anime_service switch
        {
            AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listType, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
            AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listType, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
            _                        => await _kitsuService.GetAnimeListAsync(tokenData, listType, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
        };
        return metas ?? [];
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

    // Wraps FetchListAsync in a try/catch so a single provider rate-limiting
    // or 5xx'ing doesn't abort the dashboard render — the empty result just
    // collapses the Continue Watching shelf rather than crashing the page.
    // bypassCache is retained for signature compatibility with existing
    // call sites but is now a no-op; lists are no longer cached on the
    // dashboard path (every load reflects live state).
    private async Task<List<Meta>> SafeFetchListAsync(TokenData tokenData, ListType listType,
        bool groupSeasons = true, bool bypassCache = false, bool hideUnreleased = false, bool hideAdult = false)
    {
        try
        {
            return await FetchListAsync(tokenData, listType, groupSeasons, hideUnreleased, hideAdult);
        }
        catch { return []; }
    }

    [Route("/configure")]
    [Route("{config}/configure")]
    public async Task<IActionResult> Configure(string config = null)
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
                    configUid = await _configStore.UpsertAsync(tokenData);
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

        return View(new ConfigureViewModel
        {
            TokenData = encodedTokenData,
            ConfigUid = configUid,
            ConfigRevision = configRevision,
            AnimeService = tokenData?.anime_service ?? AnimeService.Kitsu,
            AnonymousUser = anonymousUser,
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
    /// integration. Reuses the Configure action's identity + flag resolution
    /// verbatim and returns the same ConfigureViewModel; the Stremio.cshtml
    /// view just renders a different subset of sections.
    /// </summary>
    [Route("/stremio")]
    public Task<IActionResult> Stremio() => RenderConfigure("Stremio", config: null);

    /// <summary>
    /// Advanced settings page — Backups, Danger Zone, Home Server Sync.
    /// Lives off /configure so the account page stays focused on identity
    /// + linked accounts; reuses Configure's view-model so the JS handlers
    /// in _ConfigurePageScript bind the same way regardless of which view
    /// rendered them.
    /// </summary>
    [Route("/advanced")]
    public Task<IActionResult> Advanced() => RenderConfigure("Advanced", config: null);

    private async Task<IActionResult> RenderConfigure(string viewName, string config)
    {
        var result = await Configure(config);
        if (result is ViewResult viewResult)
        {
            viewResult.ViewName = viewName;
            return viewResult;
        }
        // Non-view results (RedirectResult / NotFound / etc.) flow through
        // unchanged so the Stremio route inherits Configure's redirect logic.
        return result;
    }

    /// <summary>
    /// Generates a fresh scrobble token for the given UID, invalidating any existing webhook
    /// URLs. Returns the new token so the JS can update the displayed URL without a reload.
    /// </summary>
    [HttpPost("Home/RotateScrobbleToken")]
    public async Task<JsonResult> RotateScrobbleToken([FromBody] UidRequest request)
    {
        if (string.IsNullOrEmpty(request?.uid))
            return new JsonResult(new { success = false, error = "missing uid" });

        var token = await _configStore.RotateScrobbleTokenAsync(request.uid);
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

        var metas = (await SafeFetchListAsync(token, ListType.Current, groupSeasons, /* nocache */ true, hideUnreleased, hideAdult))
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
        if (string.IsNullOrEmpty(request?.uid))
            return new JsonResult(new { success = false, error = "missing uid" });

        await _configStore.SetPlexUsernameAsync(request.uid, request.username);
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
        if (string.IsNullOrEmpty(request?.uid))
            return new JsonResult(new { success = false, error = "missing uid" });
        var manifestUrl = (request.manifestUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(manifestUrl))
            return new JsonResult(new { success = false, error = "manifest URL required" });

        var addon = await _addonStreamService.FetchManifestAsync(manifestUrl);
        if (addon == null)
        {
            return new JsonResult(new { success = false,
                error = "couldn't fetch a Stremio stream-addon manifest at that URL" });
        }

        var added = await _configStore.AddStreamAddonAsync(request.uid, addon);
        // The per-(uid, episode) fan-out cache was computed against the
        // pre-add addon list; a new provider wouldn't surface streams
        // until the TTL elapsed without this wipe.
        if (added) await _configStore.InvalidateStreamCacheAsync(request.uid);
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
        if (string.IsNullOrEmpty(request?.uid))
            return new JsonResult(new { success = false, error = "missing uid" });
        var manifestUrl = (request.manifestUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(manifestUrl))
            return new JsonResult(new { success = false, error = "manifest URL required" });

        var removed = await _configStore.RemoveStreamAddonAsync(request.uid, manifestUrl);
        // The fan-out cache still has streams from the removed provider
        // in its rows — wipe so the next watch-page render rebuilds
        // against the new addon list instead of serving stale debrid
        // URLs that route through a provider the user just kicked out.
        if (removed) await _configStore.InvalidateStreamCacheAsync(request.uid);
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
        if (string.IsNullOrEmpty(request?.uid))
            return new JsonResult(new { success = false, error = "missing uid" });
        if (request.urls == null || request.urls.Count == 0)
            return new JsonResult(new { success = false, error = "urls required" });

        var changed = await _configStore.ReorderStreamAddonsAsync(request.uid, request.urls);
        // Cached fan-out preserved the addon emit order, so a reorder
        // also needs a wipe — otherwise the source picker would keep
        // showing rows in the pre-reorder sequence for up to a full
        // TTL window after the user committed the change.
        if (changed) await _configStore.InvalidateStreamCacheAsync(request.uid);
        return new JsonResult(new { success = true, changed });
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
        if (string.IsNullOrEmpty(request?.uid))
            return new JsonResult(new { success = false, error = "missing uid" });

        var revision = await _configStore.SetFlagsAsync(request.uid, request.flags1, request.flags2, request.flags3);
        return new JsonResult(new { success = true, revision });
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

        return new JsonResult(new { success = true });
    }

    /// <summary>
    /// Resets the toggle bits to all-zero, keeping the user logged in. Bumps the revision
    /// so Stremio sees a different install URL.
    /// </summary>
    [HttpPost("Home/ResetConfig")]
    public async Task<JsonResult> ResetConfig([FromBody] UidRequest request)
    {
        if (string.IsNullOrEmpty(request?.uid))
            return new JsonResult(new { success = false, error = "missing uid" });

        var revision = await _configStore.SetFlagsAsync(request.uid, 0, 0, 0);
        return new JsonResult(new { success = true, revision });
    }

    /// <summary>
    /// Removes the configuration row from the store and ends the session. After this the
    /// user is fully signed out and any old install URLs they had become dead links.
    /// </summary>
    [HttpPost("Home/DeleteConfig")]
    public async Task<JsonResult> DeleteConfig([FromBody] UidRequest request)
    {
        if (!string.IsNullOrEmpty(request?.uid))
            await _configStore.DeleteAsync(request.uid);

        await _tokenService.RemoveCachedUser();
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
}

public class UidRequest
{
    public string uid { get; set; }
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

public class ConfigBackup
{
    public int version { get; set; }
    public string service { get; set; }
    public TokenData tokenData { get; set; }
    public BackupFlags flags { get; set; }
}

public class BackupFlags
{
    public byte flags1 { get; set; }
    public byte flags2 { get; set; }
    public byte flags3 { get; set; }
}
