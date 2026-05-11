using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// In-app browse routes for AniList entities surfaced from a detail page
    /// (a staff member's filmography, a studio's catalog). Replaces the prior
    /// behaviour of bouncing every chip click to anilist.co; the user stays
    /// inside AniSync and the resulting poster cards still hand off to the
    /// per-service Manage Entry modal the same way library cards do.
    ///
    /// Both routes are public — no auth required to browse. Anonymous
    /// visitors render exactly the same grid; only the Manage Entry hand-off
    /// requires identity.
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
        public Task<IActionResult> Staff(int id) => Browse(BrowseKind.Staff, id);

        [Route("/studio/{id:int}")]
        [Route("/studio/{id:int}/{slug?}")]
        public Task<IActionResult> Studio(int id) => Browse(BrowseKind.Studio, id);

        private async Task<IActionResult> Browse(BrowseKind kind, int id)
        {
            // Anonymous fresh-visit: synthesise a Kitsu-default token so the
            // id-translation step in AnilistFallback has a target service. The
            // upstream AniList GraphQL doesn't need auth either way.
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var (name, items) = kind == BrowseKind.Staff
                ? await _anilistFallback.GetStaffMediaAsync(id, tokenData.anime_service)
                : await _anilistFallback.GetStudioMediaAsync(id, tokenData.anime_service);

            return View("Index", new BrowseViewModel
            {
                Kind = kind,
                Id = id,
                Name = name,
                ConfigUid = uid,
                Items = items ?? [],
            });
        }
    }

    public enum BrowseKind { Staff, Studio }

    public class BrowseViewModel
    {
        public BrowseKind Kind { get; set; }
        public int Id { get; set; }
        // Resolved name from the AniList Staff/Studio root — null when the
        // id didn't resolve, in which case the view renders an "Unknown" header.
        public string Name { get; set; }
        public string ConfigUid { get; set; }
        public List<Meta> Items { get; set; } = [];
    }
}
