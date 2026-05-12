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

        /// <summary>
        /// Next-page endpoint for /studio/{id}'s infinite scroll. Returns
        /// the shared _PosterGrid partial so studio-detail-pagination.js
        /// can splice its children into the existing grid. The
        /// X-Has-Next-Page header is the authoritative end-of-list
        /// signal — Studio.media is filtered client-side to drop manga
        /// edges, so an "empty" partial can still mean more pages ahead.
        /// </summary>
        [Route("/studio/{id:int}/page")]
        public async Task<IActionResult> StudioPage(int id, int page = 1)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };

            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            var (_, items, hasNext) = await _anilistFallback.GetStudioMediaAsync(id, tokenData.anime_service, page);
            Response.Headers["X-Has-Next-Page"] = hasNext ? "true" : "false";
            return PartialView("_PosterGrid", new PosterGridViewModel
            {
                Items = items ?? new List<Meta>(),
                ConfigUid = uid,
            });
        }

        /// <summary>
        /// /studio listing — alphabetical tile grid. Renders the first
        /// AniList page server-side; subsequent pages are pulled in via
        /// <see cref="StudiosPage"/> by the infinite-scroll handler.
        /// Sits at the no-id end of the same route prefix; the :int
        /// constraints on the per-studio routes above keep this from
        /// being shadowed.
        /// </summary>
        [Route("/studio")]
        public async Task<IActionResult> Studios()
        {
            var (studios, _) = await _anilistFallback.GetStudiosListAsync(page: 1);
            return View("Studios", studios);
        }

        /// <summary>
        /// Next-page endpoint for /studio's infinite scroll. Returns the
        /// shared _StudioTiles partial so the client can splice its
        /// children into the existing grid. The X-Has-Next-Page header
        /// is the authoritative end-of-list signal — an empty partial
        /// body can still mean "more pages ahead" because client-side
        /// filtering (isAnimationStudio + non-zero anime count) can
        /// drop every entry on a given AniList page.
        /// </summary>
        [Route("/studio/page")]
        public async Task<IActionResult> StudiosPage(int page = 1)
        {
            var (studios, hasNext) = await _anilistFallback.GetStudiosListAsync(page);
            Response.Headers["X-Has-Next-Page"] = hasNext ? "true" : "false";
            return PartialView("_StudioTiles", studios ?? new List<StudioSummary>());
        }

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

            string name;
            List<Meta> items;
            if (kind == BrowseKind.Staff)
            {
                (name, items) = await _anilistFallback.GetStaffMediaAsync(id, tokenData.anime_service);
            }
            else
            {
                (name, items, _) = await _anilistFallback.GetStudioMediaAsync(id, tokenData.anime_service);
            }

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
