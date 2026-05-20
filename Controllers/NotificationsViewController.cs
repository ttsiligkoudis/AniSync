using AnimeList.Models;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Web-app page for browsing the full notifications history. The
    /// bell dropdown in the site header surfaces the latest 10 and a
    /// "View all" link to this page; the page renders the first chunk
    /// server-side and the JS appends successive chunks via the
    /// existing <c>/api/v1/notifications?skip=N</c> endpoint.
    /// </summary>
    public class NotificationsViewController : Controller
    {
        // Initial server-rendered chunk + the chunk size the
        // infinite-scroll JS asks for on each append. Mirrors the
        // Discover paginator's CatalogPageSize.
        private const int PageSize = 20;

        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;
        private readonly INotificationStore _store;
        private readonly IAnimeMappingService _mapping;

        public NotificationsViewController(
            ITokenService tokenService,
            IConfigStore configStore,
            INotificationStore store,
            IAnimeMappingService mapping)
        {
            _tokenService = tokenService;
            _configStore = configStore;
            _store = store;
            _mapping = mapping;
        }

        [HttpGet("/notifications")]
        public async Task<IActionResult> Index()
        {
            var (token, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            if (uid == null) return RedirectToAction("Index", "Home");

            var items = await _store.ListForUserAsync(uid, PageSize);
            // Translate stored anime_id / LinkPath into the user's CURRENT
            // primary service so a click after a provider swap lands on
            // /anime/{primary-service-id}/watch/... rather than the
            // creation-time service. Falls back to the stored link when
            // no cross-service mapping exists.
            await _mapping.RewriteLinksToServiceAsync(items, token.anime_service);
            return View(new NotificationsPageViewModel
            {
                Items = items,
                NextSkip = items.Count,
                HasMore = items.Count == PageSize,
                PageSize = PageSize,
            });
        }

        /// <summary>
        /// Infinite-scroll chunk renderer. JS hits this with
        /// ?skip=N — same offset semantics as
        /// /api/v1/notifications — and inserts the returned partial
        /// at the end of the list.
        /// </summary>
        [HttpGet("/notifications/page")]
        public async Task<IActionResult> Page(int skip = 0)
        {
            var (token, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            if (uid == null) return Unauthorized();

            if (skip < 0) skip = 0;
            var items = await _store.ListForUserAsync(uid, PageSize, skip);
            await _mapping.RewriteLinksToServiceAsync(items, token.anime_service);
            return PartialView("_NotificationRows", items);
        }
    }
}
