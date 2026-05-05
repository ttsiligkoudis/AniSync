using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Diagnostics;

public class HomeController : Controller
{
    private readonly ITokenService _tokenService;
    private readonly IConfigStore _configStore;

    public HomeController(ITokenService tokenService, IConfigStore configStore)
    {
        _tokenService = tokenService;
        _configStore = configStore;
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    public async Task<IActionResult> Index(string config = null)
    {
        var tokenData = await _tokenService.GetAccessTokenAsync(config);

        if (tokenData != null)
        {
            tokenData.expires_in = null;
            ViewBag.AnonymousUser = tokenData.anonymousUser;

            // Authenticated users get a v5 (UID-only) install URL: store the token JSON in
            // the config store and hand the JS just the UID. Idempotent — the same user
            // re-logging in keeps their UID. Anonymous users keep the inline (v3) flow
            // because their "token data" is just a 30-byte service preference and there's
            // no benefit to a DB row plus no stable identity to dedupe on.
            if (!tokenData.anonymousUser)
            {
                var uid = await _configStore.UpsertAsync(tokenData);
                ViewBag.ConfigUid = uid;
                // Used as cache-busting bytes in the install URL — see Index.cshtml's JS.
                ViewBag.ConfigRevision = await _configStore.GetRevisionAsync(uid);
                // Linked secondary accounts the multi-provider sync will fan writes out to.
                // The view renders a per-service Link / Unlink row from this list.
                ViewBag.LinkedTokens = await _configStore.GetLinkedTokensAsync(uid);

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
                ViewBag.TokenData = CompressToUrlSafe(SerializeObject(tokenData, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            }
        }

        ViewBag.AnimeService = tokenData?.anime_service ?? AnimeService.Kitsu;

        // Hydrate the toggle flags. The URL-config path covers Stremio's manifest deep-link
        // (v3/v4/v5 bytes in the path); the UID fallback covers everything else — direct
        // visits to /Home, redirects after primary swap, login-completion landings — so the
        // page always reflects the user's saved state instead of falling back to defaults.
        Configuration configuration = null;
        if (!string.IsNullOrEmpty(config))
        {
            configuration = await ResolveConfigAsync(config, _configStore);
        }
        else if (!string.IsNullOrEmpty((string)ViewBag.ConfigUid))
        {
            var (f1, f2, f3, _) = await _configStore.GetFlagsAsync((string)ViewBag.ConfigUid);
            configuration = new Configuration();
            ApplyBinaryFlags(configuration, f1, f2, f3);
        }

        if (configuration != null)
        {
            ViewBag.ShowCurrent = configuration.showCurrent;
            ViewBag.ShowCompleted = configuration.showCompleted;
            ViewBag.ShowTrending = configuration.showTrending;
            ViewBag.ShowSeasonal = configuration.showSeasonal;
            ViewBag.ShowPlanning = configuration.showPlanning;
            ViewBag.ShowPaused = configuration.showPaused;
            ViewBag.ShowDropped = configuration.showDropped;
            ViewBag.ShowRepeating = configuration.showRepeating;
            ViewBag.ShowAiring = configuration.showAiring;
            ViewBag.DiscoverOnlyCurrent = configuration.discoverOnlyCurrent;
            ViewBag.DiscoverOnlyCompleted = configuration.discoverOnlyCompleted;
            ViewBag.DiscoverOnlyTrending = configuration.discoverOnlyTrending;
            ViewBag.DiscoverOnlySeasonal = configuration.discoverOnlySeasonal;
            ViewBag.DiscoverOnlyPlanning = configuration.discoverOnlyPlanning;
            ViewBag.DiscoverOnlyPaused = configuration.discoverOnlyPaused;
            ViewBag.DiscoverOnlyDropped = configuration.discoverOnlyDropped;
            ViewBag.DiscoverOnlyRepeating = configuration.discoverOnlyRepeating;
            ViewBag.DiscoverOnlyAiring = configuration.discoverOnlyAiring;
            ViewBag.ShowExternalStreams = configuration.showExternalStreams;
            // Inverse-sense flags: stored as "hide/disable" so default-zero rows mean
            // "show / enabled" — the UI toggles, however, are positive ("Manage Entry",
            // "Auto-track progress") so flip the bool here.
            ViewBag.ShowManageEntry = configuration.hideManageEntry != true;
            ViewBag.AutoTrackProgress = configuration.disableAutoTrack != true;
        }

        return View();
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

    [Route("{config}/configure")]
    public IActionResult Configure(string config) => RedirectToAction("Index", new { config });
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
