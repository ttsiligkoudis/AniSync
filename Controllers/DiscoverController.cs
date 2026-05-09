using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Public-mode counterpart to <see cref="LibraryController"/>: trending / seasonal /
    /// airing catalogs work without an authenticated list account, so /discover renders
    /// for everyone — anonymous visitors and logged-in users alike. Logged-in users see
    /// the data through their primary service so the Manage Entry hand-off works on the
    /// resulting cards; anonymous visitors fall back to Kitsu (matching the addon's
    /// anonymous default — see HomeController.Configure).
    /// </summary>
    public class DiscoverController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly IConfigStore _configStore;

        public DiscoverController(
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

        [Route("/discover")]
        public async Task<IActionResult> Index()
        {
            // Anonymous fresh-visit: GetAccessTokenAsync returns null. Synthesise an
            // anonymous TokenData with the Kitsu default so the per-service dispatch
            // below has something to switch on. ListType.Trending_Desc doesn't need a
            // user identity — the underlying GraphQL/REST calls run unauthenticated.
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu, anonymousUser = true };

            // Resolve the row's UID for logged-in users so per-card Manage Entry links
            // hit the existing config-scoped flow. Anonymous users get null and the
            // view renders the cards as inert (no Manage Entry — they have no list).
            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var metas = tokenData.anime_service switch
            {
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, ListType.Trending_Desc),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, ListType.Trending_Desc),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, ListType.Trending_Desc),
            };

            return View(new DiscoverViewModel
            {
                ConfigUid = uid,
                AnimeService = tokenData.anime_service,
                AnonymousUser = tokenData.anonymousUser,
                Trending = metas ?? [],
            });
        }
    }

    /// <summary>
    /// View model for the discover page (Views/Discover/Index.cshtml).
    /// </summary>
    public class DiscoverViewModel
    {
        public string ConfigUid { get; set; }
        public AnimeService AnimeService { get; set; }
        public bool AnonymousUser { get; set; }
        public List<Meta> Trending { get; set; } = [];
    }
}
