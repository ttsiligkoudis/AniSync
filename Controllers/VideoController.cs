using AnimeList.Models;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// The "video" section — general movies &amp; series, sourced from
    /// Cinemeta rather than the anime trackers. Lives alongside the anime
    /// surfaces (Discover / Anime) but is intentionally self-contained: its
    /// own controller, its own <c>Views/Video/</c> folder and its own
    /// <c>/movies</c> · <c>/series</c> · <c>/movie|series/{id}</c> URL space,
    /// so the boundary with the anime feature is clear without paying the cost
    /// of a separate project / ASP.NET Area.
    ///
    /// Browsing + detail are public (no list account needed). The watch page
    /// reuses the shared player view (Views/Anime/Watch.cshtml) and the id-keyed
    /// stream / subtitle endpoints on AnimeController — those work for any IMDb
    /// id, so a configured stream addon resolves debrid sources for movies the
    /// same way it does for anime.
    /// </summary>
    public class VideoController : Controller
    {
        private readonly ICinemetaService _cinemeta;
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;
        private readonly ILogger<VideoController> _logger;

        public VideoController(
            ICinemetaService cinemeta,
            ITokenService tokenService,
            IConfigStore configStore,
            ILogger<VideoController> logger)
        {
            _cinemeta = cinemeta;
            _tokenService = tokenService;
            _configStore = configStore;
            _logger = logger;
        }

        // Cinemeta pages its catalogs in blocks of 100. The browse JS sends
        // 1-indexed page numbers; we convert page → item offset with this.
        private const int CatalogPageSize = 100;

        // Hand-curated genre picker — the intersection of Cinemeta's movie /
        // series genre lists, ordered by rough popularity. Avoids an extra
        // manifest round-trip per browse render and keeps the dropdown
        // consistent across both types.
        private static readonly string[] VideoGenres =
        [
            "Action", "Adventure", "Animation", "Comedy", "Crime",
            "Documentary", "Drama", "Family", "Fantasy", "History",
            "Horror", "Mystery", "Romance", "Sci-Fi", "Thriller", "War",
        ];

        [Route("/movies")]
        public Task<IActionResult> Movies(string genre = null, string search = null)
            => Browse("movie", genre, search);

        [Route("/series")]
        public Task<IActionResult> Series(string genre = null, string search = null)
            => Browse("series", genre, search);

        private async Task<IActionResult> Browse(string type, string genre, string search)
        {
            var uid = await ResolveUidAsync();
            var hasSearch = !string.IsNullOrWhiteSpace(search);

            // Browse (popularity) loads paint behind a skeleton + a client-side
            // page-1 fetch (video-pagination.js) so the initial render isn't
            // blocked on Cinemeta — same pattern /discover uses. Search is
            // single-shot (Cinemeta's relevance list isn't paginated here), so
            // we fetch + render it server-side.
            List<Meta> items = [];
            if (hasSearch)
            {
                items = await _cinemeta.GetVideoCatalogAsync(type, genre, search.Trim());
            }

            return View("Index", new VideoBrowseViewModel
            {
                Type = type,
                ConfigUid = uid,
                Genre = string.IsNullOrWhiteSpace(genre) ? null : genre.Trim(),
                Search = hasSearch ? search.Trim() : null,
                AvailableGenres = VideoGenres,
                Items = items,
                NeedsClientLoad = !hasSearch,
            });
        }

        /// <summary>
        /// Infinite-scroll pagination endpoint for the browse grids. Returns
        /// just the next chunk of poster cards via the shared _PosterGrid
        /// partial (VideoLinks on so cards route to /movie|series/{id}).
        /// video-pagination.js drives this; the wire is 1-indexed pages.
        /// </summary>
        [Route("/video/page")]
        public async Task<IActionResult> Page(string type, string genre = null, int page = 1)
        {
            if (type != "movie" && type != "series") type = "movie";
            if (page < 1) page = 1;
            var skip = (page - 1) * CatalogPageSize;

            var uid = await ResolveUidAsync();
            var items = await _cinemeta.GetVideoCatalogAsync(type, genre, search: null, skip: skip);

            return PartialView("_PosterGrid", new PosterGridViewModel
            {
                Items = items,
                ConfigUid = uid,
                VideoLinks = true,
            });
        }

        [Route("/movie/{id}")]
        public Task<IActionResult> MovieDetail(string id) => Detail("movie", id);

        [Route("/series/{id}")]
        public Task<IActionResult> SeriesDetail(string id) => Detail("series", id);

        private async Task<IActionResult> Detail(string type, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                Response.StatusCode = 404;
                return View("NotFound");
            }

            var meta = await _cinemeta.GetVideoMetaAsync(type, id);
            if (meta == null)
            {
                Response.StatusCode = 404;
                return View("NotFound");
            }

            var uid = await ResolveUidAsync();

            return View("Detail", new VideoDetailViewModel
            {
                Type = type,
                Meta = meta,
                ConfigUid = uid,
            });
        }

        [Route("/movie/{id}/watch")]
        public Task<IActionResult> MovieWatch(string id) => Watch("movie", id, 1, null);

        [Route("/series/{id}/watch/{episode:int}")]
        [Route("/series/{id}/watch/{season:int}/{episode:int}")]
        public Task<IActionResult> SeriesWatch(string id, int episode, int? season = null)
            => Watch("series", id, episode, season);

        private async Task<IActionResult> Watch(string type, string id, int episode, int? season)
        {
            if (string.IsNullOrEmpty(id) || episode <= 0)
            {
                Response.StatusCode = 404;
                return View("NotFound");
            }

            var (tokenData, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            tokenData ??= new TokenData { anime_service = AnimeService.Kitsu };

            var meta = await _cinemeta.GetVideoMetaAsync(type, id);
            if (meta == null)
            {
                Response.StatusCode = 404;
                return View("NotFound");
            }

            if (type == "movie")
            {
                // Movies collapse to a single streamable unit — synthesise one
                // Video for episode 1 so the shared per-episode Watch view
                // renders. Anything other than /watch on a movie has already
                // routed here as episode 1.
                meta.videos = [new Video
                {
                    episode = 1,
                    season = 1,
                    title = meta.name,
                    thumbnail = meta.poster ?? meta.background,
                }];
            }
            else if (meta.videos == null || meta.videos.Count == 0)
            {
                Response.StatusCode = 404;
                return View("NotFound");
            }

            // Cinemeta's season/episode ARE the IMDb coordinates the stream
            // addons expect — no cour normalisation (unlike the anime path),
            // so we match the requested episode directly. season null means
            // "any cour" for the common single-season case.
            var current = type == "movie"
                ? meta.videos[0]
                : meta.videos.FirstOrDefault(v =>
                    v.episode == episode &&
                    (season == null || (v.season > 0 ? v.season : 1) == season.Value));
            if (current == null)
            {
                Response.StatusCode = 404;
                return View("NotFound");
            }

            var (prev, next) = ComputePrevNext(meta.videos, current, type);

            bool hasAddons = false;
            if (!string.IsNullOrEmpty(uid))
            {
                hasAddons = (await _configStore.GetStreamAddonsAsync(uid)).Count > 0;
            }

            return View("/Views/Anime/Watch.cshtml", new WatchViewModel
            {
                Anime = meta,
                Current = current,
                Prev = prev,
                Next = next,
                ConfigUid = uid,
                AnonymousUser = tokenData.anonymousUser,
                HasStreamAddons = hasAddons,
                // External-services links are anime-tracker-sourced; the video
                // section has no equivalent, so the source panel relies on
                // stream addons alone.
                ExternalStreamsEnabled = false,
                BasePath = type == "movie" ? "/movie" : "/series",
            });
        }

        /// <summary>
        /// Resolves the current session's config UID (null for anonymous /
        /// unauthenticated visitors). Browse + detail render fine without one;
        /// it gates the per-user stream-addon lookup on the watch page.
        /// </summary>
        private async Task<string> ResolveUidAsync()
        {
            // Name both elements rather than discarding the first — the
            // discard form can't infer the tuple element types here.
            var (tokenData, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            _ = tokenData;
            return uid;
        }

        // Prev/next neighbours in (season, episode) order. Movies never have
        // neighbours. Mirrors the anime watch nav but without the future-
        // episode skipping — Cinemeta's `released` dates aren't reliable enough
        // for upcoming-episode gating on general series, and the watch page
        // already no-ops gracefully on a stale link.
        private static (Video Prev, Video Next) ComputePrevNext(List<Video> videos, Video current, string type)
        {
            if (type == "movie") return (null, null);

            var ordered = videos
                .OrderBy(v => v.season > 0 ? v.season : 1)
                .ThenBy(v => v.episode)
                .ToList();
            var idx = ordered.FindIndex(v =>
                v.episode == current.episode &&
                (v.season > 0 ? v.season : 1) == (current.season > 0 ? current.season : 1));
            if (idx < 0) return (null, null);

            var prev = idx > 0 ? ordered[idx - 1] : null;
            var next = idx < ordered.Count - 1 ? ordered[idx + 1] : null;
            return (prev, next);
        }
    }

    /// <summary>
    /// View model for the video browse pages (Views/Video/Index.cshtml).
    /// Type is the Cinemeta content type ("movie" / "series") and drives the
    /// page title, active-nav highlight and the pagination endpoint's type
    /// parameter.
    /// </summary>
    public class VideoBrowseViewModel
    {
        public string Type { get; set; }
        public string ConfigUid { get; set; }
        public string Genre { get; set; }
        public string Search { get; set; }
        public IReadOnlyList<string> AvailableGenres { get; set; } = [];
        public List<Meta> Items { get; set; } = [];
        // True for the popularity browse — the view emits skeleton placeholders
        // and video-pagination.js fetches page 1 on load. False for search,
        // which is rendered server-side from Items.
        public bool NeedsClientLoad { get; set; }
    }

    /// <summary>
    /// View model for the video detail page (Views/Video/Detail.cshtml).
    /// </summary>
    public class VideoDetailViewModel
    {
        public string Type { get; set; }
        public Meta Meta { get; set; }
        public string ConfigUid { get; set; }
    }
}
