using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using System.Diagnostics;

public class HomeController : Controller
{
    // Maximum number of "Continue watching" tiles surfaced on the dashboard. Six fits
    // 2-3 rows on the dashboard's narrow main column without scrolling, and matches
    // the kind of "what you were last watching" shape users expect on home screens —
    // not a full list, just the most recent few.
    private const int ContinueWatchingMaxItems = 6;

    private readonly ITokenService _tokenService;
    private readonly IConfigStore _configStore;
    private readonly IAnilistService _anilistService;
    private readonly IKitsuService _kitsuService;
    private readonly IMalService _malService;
    private readonly IAnilistFallback _anilistFallback;
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
        IUserListCache listCache,
        IMemoryCache dashboardCache)
    {
        _tokenService = tokenService;
        _configStore = configStore;
        _anilistService = anilistService;
        _kitsuService = kitsuService;
        _malService = malService;
        _anilistFallback = anilistFallback;
        _listCache = listCache;
        _dashboardCache = dashboardCache;
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
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
        List<Meta> continueWatching = [];
        bool hasStats = false;
        int watchingTotal = 0;
        int completedTotal = 0;
        int totalHoursWatched = 0;
        double? meanScore = null;
        List<string> contributingNames = [];

        // Continue-watching + stats surfaces only fire for non-anonymous logged-in
        // users. Anonymous and not-logged-in visitors get the plain three-tile
        // dashboard; they have nothing to "continue" and no list to compute stats
        // from.
        if (tokenData != null && !tokenData.anonymousUser)
        {
            var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
            uid = resolved;

            // Site UI is always ungrouped — the enableSeasonGrouping pref now
            // only governs Stremio's catalog / meta endpoints. On the dashboard
            // we always want each cour visible as its own card so Continue
            // Watching matches what the user sees in /library.
            const bool groupSeasons = false;

            // Pick the AniList token (primary or linked) — stats now go
            // through AniList's User.statistics GraphQL, which is a single
            // query that's vastly cheaper than fetching the full Watching +
            // Completed lists. Users without an AniList account see the
            // stats panel hidden (HasStats = false); they can link AniList
            // from /configure to unlock it.
            TokenData anilistTokenForStats = null;
            if (tokenData.anime_service == AnimeService.Anilist)
            {
                anilistTokenForStats = tokenData;
            }
            else if (!string.IsNullOrEmpty(uid))
            {
                var linkedTokens = await _configStore.GetLinkedTokensAsync(uid);
                foreach (var lt in linkedTokens)
                {
                    if (lt.NeedsReauth || lt.TokenData == null || lt.TokenData.anonymousUser) continue;
                    if (lt.Service != AnimeService.Anilist) continue;
                    anilistTokenForStats = lt.TokenData;
                    break;
                }
            }

            // Fire the AniList stats call alongside the primary's Watching
            // fetch. Continue Watching still comes from the primary so the
            // "Ep N / Total" progress is meaningful to the user (per-service
            // progress differs based on which service they actively update).
            // Lists are no longer cached — every dashboard hit reflects live
            // state.
            var statsTask = anilistTokenForStats != null
                ? _anilistService.GetUserStatsAsync(anilistTokenForStats)
                : Task.FromResult<AnilistUserStats?>(null);
            var watchingTask = SafeFetchListAsync(tokenData, ListType.Current, groupSeasons, /* nocache */ true);
            await Task.WhenAll(statsTask, watchingTask);

            continueWatching = watchingTask.Result.Take(ContinueWatchingMaxItems).ToList();

            var stats = await statsTask;
            if (stats != null)
            {
                hasStats = true;
                watchingTotal = stats.Watching;
                completedTotal = stats.Completed;
                totalHoursWatched = stats.TotalHoursWatched;
                meanScore = stats.MeanScore;
                contributingNames.Add(AnimeService.Anilist.ToString());
            }
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
        var popularThisSeasonTask = GetPopularBySeasonAsync(SeasonCurrent);
        var mostAnticipatedTask = GetPopularBySeasonAsync(SeasonNext);
        var newEpisodesTodayTask = _anilistFallback.GetNewEpisodesTodayAsync();
        await Task.WhenAll(seasonStatsTask, popularThisSeasonTask, mostAnticipatedTask, newEpisodesTodayTask);

        var (seasonAiring, seasonNew, seasonTotal) = await seasonStatsTask;

        return View(new DashboardViewModel
        {
            TokenData = tokenData,
            ConfigUid = uid,
            ContinueWatching = continueWatching,
            HasStats = hasStats,
            WatchingTotal = watchingTotal,
            CompletedTotal = completedTotal,
            TotalHoursWatched = totalHoursWatched,
            MeanScore = meanScore,
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
    private async Task<List<Meta>> FetchListAsync(TokenData tokenData, ListType listType, bool groupSeasons = true)
    {
        var metas = tokenData.anime_service switch
        {
            AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, listType, groupSeasons: groupSeasons),
            AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listType, groupSeasons: groupSeasons),
            _                        => await _kitsuService.GetAnimeListAsync(tokenData, listType, groupSeasons: groupSeasons),
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
    private async Task<List<Meta>> GetPopularBySeasonAsync(string seasonOption)
    {
        var (season, year) = GetSeasonAndYear(seasonOption);
        var cacheKey = $"dashboard:popular-by-season:{season}:{year}";

        if (_dashboardCache.TryGetValue<List<Meta>>(cacheKey, out var cached) && cached != null)
            return cached;

        try
        {
            var list = await _anilistService.GetAnimeListAsync(
                tokenData: null,
                list: ListType.Seasonal,
                genre: seasonOption,
                groupSeasons: false);

            var top = list?.Take(15).ToList() ?? [];

            if (top.Count > 0)
            {
                _dashboardCache.Set(cacheKey, top, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = PopularBySeasonCacheDuration,
                });
            }
            return top;
        }
        catch
        {
            return [];
        }
    }

    // Wraps FetchListAsync in a try/catch so a single provider rate-limiting
    // or 5xx'ing doesn't abort the dashboard render — the empty result just
    // collapses the Continue Watching shelf rather than crashing the page.
    // bypassCache is retained for signature compatibility with existing
    // call sites but is now a no-op; lists are no longer cached on the
    // dashboard path (every load reflects live state).
    private async Task<List<Meta>> SafeFetchListAsync(TokenData tokenData, ListType listType,
        bool groupSeasons = true, bool bypassCache = false)
    {
        try
        {
            return await FetchListAsync(tokenData, listType, groupSeasons);
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

                // Hydrate the session from the config-URL-derived tokenData when the user
                // arrives via a v5 install URL (or any path that resolves identity from the
                // store rather than the cookie). Without this, post-login endpoints —
                // SetPrimary, SyncNow, LinkProvider, etc. — would all bail with "log in with
                // a primary provider first" because they read the session's AccessToken.
                // The config URL is already a per-user bearer token, so trusting it as a
                // login signal is consistent with the rest of the app.
                HttpContext.Session.SetString("AccessToken", SerializeObject(tokenData));
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
        // (v3/v4/v5 bytes in the path); the UID fallback covers everything else — direct
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
