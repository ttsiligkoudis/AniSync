using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// /staff/{id} drill-down — every anime the staff member is credited
    /// on, rendered as a poster grid. Sibling browse surfaces (studios,
    /// tags) live under DiscoverController at /discover/studio and
    /// /discover/tag so the URL surface tracks the user-facing
    /// "Browse By" affordances on the home page.
    ///
    /// Route is public — no auth required. Anonymous visitors render the
    /// same grid; only the per-card Manage Entry hand-off requires identity.
    /// </summary>
    public class BrowseController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistFallback _anilistFallback;
        private readonly IConfigStore _configStore;

        public BrowseController(
            ITokenService tokenService,
            IAnilistFallback anilistFallback,
            IConfigStore configStore)
        {
            _tokenService = tokenService;
            _anilistFallback = anilistFallback;
            _configStore = configStore;
        }

        [Route("/staff/{id:int}")]
        [Route("/staff/{id:int}/{slug?}")]
        public async Task<IActionResult> Staff(int id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var (name, items) = await _anilistFallback.GetStaffMediaAsync(id, tokenData.anime_service);

            return View("Staff", new BrowseViewModel
            {
                Id = id,
                Name = name,
                ConfigUid = uid,
                Items = items ?? [],
            });
        }
    }

    public class BrowseViewModel
    {
        public int Id { get; set; }
        // Resolved name from the AniList Staff root — null when the id
        // didn't resolve, in which case the view renders an "Unknown" header.
        public string Name { get; set; }
        public string ConfigUid { get; set; }
        public List<Meta> Items { get; set; } = [];
    }
}
