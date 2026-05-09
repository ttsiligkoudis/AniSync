using AnimeList.Models;
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

        public LibraryController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IConfigStore configStore)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
        }

        [Route("/library")]
        public async Task<IActionResult> Index()
        {
            // Session-based identity. /library has no path-config — it's pure web app.
            // Anonymous visitors and not-logged-in sessions get bounced to the dashboard
            // because "Currently Watching" requires a real list account; the dashboard
            // already explains that and offers a Configure-page CTA where they can log in.
            var tokenData = await _tokenService.GetAccessTokenAsync();
            if (tokenData == null || tokenData.anonymousUser)
                return RedirectToAction("Index", "Home");

            // Resolve the row's UID so the Manage Entry links on each card can use the
            // existing config-scoped /{config}/Meta/ManageEntry/{id} flow without us
            // having to surface the UID in the visible URL — it stays an implementation
            // detail of the link href.
            var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);

            // Same dispatch table CatalogController uses — keeps the data layer single-
            // sourced. groupSeasons=true matches the addon default; the user's per-config
            // disableSeasonGrouping toggle is a Stremio-side concern, not relevant here.
            var metas = tokenData.anime_service switch
            {
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, ListType.Current),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, ListType.Current),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, ListType.Current),
            };

            return View(new LibraryViewModel
            {
                ConfigUid = uid,
                AnimeService = tokenData.anime_service,
                CurrentlyWatching = metas ?? [],
            });
        }
    }

    /// <summary>
    /// View model for the library page (Views/Library/Index.cshtml). Carries the
    /// resolved UID so per-card Manage Entry links can hit the existing config-scoped
    /// route, plus the user's primary service for header rendering.
    /// </summary>
    public class LibraryViewModel
    {
        public string ConfigUid { get; set; }
        public AnimeService AnimeService { get; set; }
        public List<Meta> CurrentlyWatching { get; set; } = [];
    }
}
