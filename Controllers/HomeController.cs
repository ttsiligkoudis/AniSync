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
                configUid = await _configStore.UpsertAsync(tokenData);
                // Used as cache-busting bytes in the install URL — see Index.cshtml's JS.
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
        // visits to /Home, redirects after primary swap, login-completion landings — so the
        // page always reflects the user's saved state instead of falling back to defaults.
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
