using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Diagnostics;

public class HomeController : Controller
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ITokenService _tokenService;

    public HomeController(IHttpClientFactory clientFactory, ITokenService tokenService)
    {
        _clientFactory = clientFactory;
        _tokenService = tokenService;
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
            if (tokenData.anime_service == AnimeService.Kitsu)
            {
                tokenData.access_token = null;
                tokenData.refresh_token = null;
            }

            ViewBag.AnonymousUser = tokenData.anonymousUser;
            ViewBag.TokenData = Uri.EscapeDataString(CompressString(SerializeObject(tokenData, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore })));
        }

        ViewBag.AnimeService = tokenData?.anime_service ?? AnimeService.Kitsu;

        if (!string.IsNullOrEmpty(config))
        {
            var configuration = DeserializeObject<Configuration>(config);

            ViewBag.ShowCurrent = configuration?.showCurrent;
            ViewBag.ShowCompleted = configuration?.showCompleted;
            ViewBag.ShowTrending = configuration?.showTrending;
            ViewBag.ShowSeasonal = configuration?.showSeasonal;
            ViewBag.DiscoverOnlyCurrent = configuration?.discoverOnlyCurrent;
            ViewBag.DiscoverOnlyCompleted = configuration?.discoverOnlyCompleted;
            ViewBag.DiscoverOnlyTrending = configuration?.discoverOnlyTrending;
            ViewBag.DiscoverOnlySeasonal = configuration?.discoverOnlySeasonal;
        }

        return View();
    }

    [Route("{config}/configure")]
    public async Task<IActionResult> Configure(string config)
    {
        return RedirectToAction("Index", new { config });
    }
}
