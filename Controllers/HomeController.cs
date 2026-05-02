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

            // Authenticated users get a v4 (UID-based) install URL: store the token JSON in
            // the config store and hand the JS just the UID. Idempotent — the same user
            // re-logging in keeps their UID. Anonymous users keep the inline (v3) flow
            // because their "token data" is just a 30-byte service preference and there's
            // no benefit to a DB row plus no stable identity to dedupe on.
            if (!tokenData.anonymousUser)
            {
                ViewBag.ConfigUid = await _configStore.UpsertAsync(tokenData);
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
            var configuration = DecodeConfig(config);

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

    [Route("{config}/configure")]
    public IActionResult Configure(string config) => RedirectToAction("Index", new { config });
}
