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

        // The three discover catalogs. Order is the tab strip rendering order;
        // Trending leads because it's the most-viewed catalog in the addon's
        // existing manifest analytics.
        private static readonly ListType[] DiscoverListTypes =
        [
            ListType.Trending_Desc,
            ListType.Seasonal,
            ListType.Airing,
        ];

        [Route("/discover")]
        public async Task<IActionResult> Index(string list = null)
        {
            // Anonymous fresh-visit: GetAccessTokenAsync returns null. Synthesise an
            // anonymous TokenData with the Kitsu default so the per-service dispatch
            // below has something to switch on. The trending / seasonal / airing
            // catalogs don't need a user identity — the underlying GraphQL/REST calls
            // run unauthenticated. anonymousUser is a computed property (empty
            // username on Kitsu / empty access_token on the OAuth services) so
            // leaving the identity fields blank here makes it return true on its own.
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            var activeList = ParseListType(list);

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
                AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(tokenData, activeList),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, activeList),
                _                        => await _kitsuService.GetAnimeListAsync(tokenData, activeList),
            };

            return View(new DiscoverViewModel
            {
                ConfigUid = uid,
                AnimeService = tokenData.anime_service,
                AnonymousUser = tokenData.anonymousUser,
                ActiveList = activeList,
                Tabs = DiscoverListTypes,
                Items = metas ?? [],
            });
        }

        private static ListType ParseListType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return ListType.Trending_Desc;
            if (Enum.TryParse<ListType>(raw, ignoreCase: true, out var parsed)
                && Array.IndexOf(DiscoverListTypes, parsed) >= 0)
                return parsed;
            // Allow ?list=trending as the URL-friendly synonym for Trending_Desc — the
            // underscore in the enum name is a code-side artifact users shouldn't have
            // to know about.
            if (string.Equals(raw, "trending", StringComparison.OrdinalIgnoreCase))
                return ListType.Trending_Desc;
            return ListType.Trending_Desc;
        }
    }

    /// <summary>
    /// View model for the discover page (Views/Discover/Index.cshtml). Mirrors
    /// LibraryViewModel's shape so the shared partial + tab strip work identically.
    /// </summary>
    public class DiscoverViewModel
    {
        public string ConfigUid { get; set; }
        public AnimeService AnimeService { get; set; }
        public bool AnonymousUser { get; set; }
        public ListType ActiveList { get; set; }
        public IReadOnlyList<ListType> Tabs { get; set; } = [];
        public List<Meta> Items { get; set; } = [];
    }
}
