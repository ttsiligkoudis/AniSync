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

        if (!string.IsNullOrEmpty(config))
        {
            // ResolveConfigAsync hydrates the flag bits from the store for v5 URLs (where
            // the bytes in the URL only carry the UID). For v3/v4 the flags come from the
            // URL bytes inline.
            var configuration = await ResolveConfigAsync(config, _configStore);

            ViewBag.ShowCurrent = configuration?.showCurrent;
            ViewBag.ShowCompleted = configuration?.showCompleted;
            ViewBag.ShowTrending = configuration?.showTrending;
            ViewBag.ShowSeasonal = configuration?.showSeasonal;
            ViewBag.ShowPlanning = configuration?.showPlanning;
            ViewBag.ShowPaused = configuration?.showPaused;
            ViewBag.ShowDropped = configuration?.showDropped;
            ViewBag.ShowRepeating = configuration?.showRepeating;
            ViewBag.ShowAiring = configuration?.showAiring;
            ViewBag.DiscoverOnlyCurrent = configuration?.discoverOnlyCurrent;
            ViewBag.DiscoverOnlyCompleted = configuration?.discoverOnlyCompleted;
            ViewBag.DiscoverOnlyTrending = configuration?.discoverOnlyTrending;
            ViewBag.DiscoverOnlySeasonal = configuration?.discoverOnlySeasonal;
            ViewBag.DiscoverOnlyPlanning = configuration?.discoverOnlyPlanning;
            ViewBag.DiscoverOnlyPaused = configuration?.discoverOnlyPaused;
            ViewBag.DiscoverOnlyDropped = configuration?.discoverOnlyDropped;
            ViewBag.DiscoverOnlyRepeating = configuration?.discoverOnlyRepeating;
            ViewBag.DiscoverOnlyAiring = configuration?.discoverOnlyAiring;
            ViewBag.ShowExternalStreams = configuration?.showExternalStreams;
        }

        return View();
    }

    /// <summary>
    /// Persists the toggle bits to the config store for the given UID, so a re-install in
    /// Stremio isn't needed when changing catalog flags. Only meaningful for v5 installs;
    /// for v3 (anonymous, inline) URLs the flags live in the URL itself.
    /// </summary>
    [HttpPost("Home/SaveConfig")]
    public async Task<JsonResult> SaveConfig([FromBody] SaveConfigRequest request)
    {
        if (string.IsNullOrEmpty(request?.uid))
            return new JsonResult(new { success = false, error = "missing uid" });

        // SetFlagsAsync also bumps the revision counter; we return it so the JS can update
        // the install URL with new cache-busting bytes — Stremio refuses to refetch the
        // manifest for an already-known URL, so we have to make the URL visibly change.
        var revision = await _configStore.SetFlagsAsync(request.uid, request.flags1, request.flags2, request.flags3);
        return new JsonResult(new { success = true, revision });
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
