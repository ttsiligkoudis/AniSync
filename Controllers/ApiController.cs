using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Public read-only API surface (Phase 1). Wraps the same per-service helpers the
    /// Stremio endpoints use, but returns plain JSON without the addon-protocol envelope
    /// so a non-Stremio client (custom UI, shortcut, scripting) can consume the data.
    ///
    /// All endpoints here are anonymous — they call each provider with <c>null</c> token
    /// data, falling through to the public client-id / unauthenticated query path.
    /// User-scoped endpoints (list CRUD, sync) ship in a later phase behind the
    /// <c>{config}</c> UID.
    /// </summary>
    [ApiController]
    [Route("api/v1")]
    public class ApiController : ControllerBase
    {
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly ITmdbService _tmdbService;
        private readonly ICinemetaService _cinemetaService;
        private readonly IAnimeMappingService _mappingService;
        private readonly IAnilistFallback _anilistFallback;
        private readonly IAniSkipService _aniSkipService;
        private readonly IFillerListService _fillerListService;
        private readonly ILogger<ApiController> _logger;

        public ApiController(
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            ITmdbService tmdbService,
            ICinemetaService cinemetaService,
            IAnimeMappingService mappingService,
            IAnilistFallback anilistFallback,
            IAniSkipService aniSkipService,
            IFillerListService fillerListService,
            ILogger<ApiController> logger)
        {
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _tmdbService = tmdbService;
            _cinemetaService = cinemetaService;
            _mappingService = mappingService;
            _anilistFallback = anilistFallback;
            _aniSkipService = aniSkipService;
            _fillerListService = fillerListService;
            _logger = logger;
        }

        /// <summary>
        /// Cross-service id resolution. Pass any prefixed id (<c>tt…</c>, <c>mal:N</c>,
        /// <c>kitsu:N</c>, <c>anilist:N</c>, <c>tmdb:N</c>) and get back the full mapping
        /// — the other services' ids plus the Fribb season number when known.
        /// </summary>
        [HttpGet("mappings/{id}")]
        public async Task<IActionResult> Mappings(string id)
        {
            try
            {
                AnimeIdMapping mapping = null;
                if (id.StartsWith(imdbPrefix))
                {
                    var entries = await _mappingService.GetImdbMapping(id);
                    return new JsonResult(new { mappings = entries });
                }
                if (id.StartsWith(tmdbPrefix))
                {
                    var entries = await _mappingService.GetTmdbMapping(id);
                    return new JsonResult(new { mappings = entries });
                }
                if (id.StartsWith(malPrefix))      mapping = await _mappingService.GetMalMapping(id);
                else if (id.StartsWith(kitsuPrefix))    mapping = await _mappingService.GetKitsuMapping(id);
                else if (id.StartsWith(anilistPrefix))  mapping = await _mappingService.GetAnilistMapping(id);

                if (mapping == null) return NotFound(new { error = "no mapping for id" });
                return new JsonResult(mapping);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Mappings failed (id={Id}).", id);
                return StatusCode(500, new { error = "lookup failed" });
            }
        }

        /// <summary>
        /// Unified anime detail. The id's prefix selects the upstream provider — IMDb /
        /// TMDB hit Cinemeta and TMDB respectively; <c>mal:</c> / <c>kitsu:</c> /
        /// <c>anilist:</c> hit each provider's API and enrich the videos array with
        /// Cinemeta data when an IMDb mapping is available.
        /// </summary>
        [HttpGet("anime/{id}")]
        public async Task<IActionResult> Anime(string id)
        {
            try
            {
                if (id.StartsWith(imdbPrefix))
                {
                    var raw = await _cinemetaService.GetAnimeByIdAsync(null, id);
                    return raw == null
                        ? NotFound(new { error = "not found on cinemeta" })
                        : Content(raw, "application/json");
                }

                Meta meta = null;
                if (id.StartsWith(malPrefix))           meta = await _malService.GetAnimeByIdAsync(id, null);
                else if (id.StartsWith(kitsuPrefix))    meta = await _kitsuService.GetAnimeByIdAsync(id, null);
                else if (id.StartsWith(anilistPrefix))  meta = await _anilistService.GetAnimeByIdAsync(id, null);
                else if (id.StartsWith(tmdbPrefix))     meta = await _tmdbService.GetAnimeByIdAsync(id, null);

                if (meta == null) return NotFound(new { error = "not found" });
                return new JsonResult(new { meta });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Anime failed (id={Id}).", id);
                return StatusCode(500, new { error = "lookup failed" });
            }
        }

        /// <summary>
        /// "Audience also liked" recommendations. Powered by AniList anonymously and
        /// translated back to the requested target service so the returned ids are
        /// usable by the caller. Defaults to AniList ids.
        /// </summary>
        [HttpGet("anime/{id}/similar")]
        public async Task<IActionResult> Similar(string id, AnimeService service = AnimeService.Anilist)
        {
            try
            {
                var anilistRaw = await _mappingService.GetIdByService(id, AnimeService.Anilist);
                if (!int.TryParse(anilistRaw, out var anilistId))
                    return NotFound(new { error = "no anilist mapping for id" });

                var links = await _anilistFallback.GetRecommendationsAsync(anilistId, service);
                return new JsonResult(new { similar = links });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Similar failed (id={Id}, service={Service}).", id, service);
                return StatusCode(500, new { error = "lookup failed" });
            }
        }

        /// <summary>
        /// Streaming destinations (Crunchyroll, Netflix, HiDive, …). MAL has no native
        /// streaming-link field, so for <c>mal:</c> ids we fall through to AniList via
        /// the cross-service mapping — same behaviour as the Stremio "External services"
        /// stream button.
        /// </summary>
        [HttpGet("anime/{id}/streams")]
        public async Task<IActionResult> Streams(string id)
        {
            try
            {
                List<StreamingLink> links;
                if (id.StartsWith(malPrefix))
                    links = await _malService.GetExternalLinksAsync(id, null);
                else if (id.StartsWith(kitsuPrefix))
                    links = await _kitsuService.GetExternalLinksAsync(id, null);
                else if (id.StartsWith(anilistPrefix))
                    links = await _anilistService.GetExternalLinksAsync(id, null);
                else
                {
                    // Fall back to AniList through the mapping for IMDb / TMDB ids.
                    var anilistRaw = await _mappingService.GetIdByService(id, AnimeService.Anilist);
                    if (!int.TryParse(anilistRaw, out var anilistId))
                        return NotFound(new { error = "no anilist mapping for id" });
                    links = await _anilistFallback.GetExternalLinksAsync(anilistId);
                }

                return new JsonResult(new { streams = links ?? [] });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Streams failed (id={Id}).", id);
                return StatusCode(500, new { error = "lookup failed" });
            }
        }

        /// <summary>
        /// Unified search across providers. The <paramref name="service"/> parameter picks
        /// which provider's search endpoint to query (default: AniList for the broadest
        /// coverage). Returns the same Meta shape the catalog endpoints emit.
        /// </summary>
        [HttpGet("search")]
        public async Task<IActionResult> Search(string q, AnimeService service = AnimeService.Anilist, string skip = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(q))
                    return new JsonResult(new { results = Array.Empty<Meta>() });

                var metas = service switch
                {
                    AnimeService.Kitsu => await _kitsuService.GetAnimeListAsync(null, ListType.Search, skip, search: q),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(null, ListType.Search, skip, search: q),
                    _ => await _anilistService.GetAnimeListAsync(null, ListType.Search, skip, search: q),
                };
                return new JsonResult(new { results = metas });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Search failed (q={Q}, service={Service}).", q, service);
                return StatusCode(500, new { error = "search failed" });
            }
        }

        /// <summary>
        /// Discovery catalogs: <c>trending</c>, <c>seasonal</c>, <c>airing</c>. The
        /// <paramref name="genre"/> param doubles as the season selector ("This Season",
        /// "Next Season", "Previous Season") for the seasonal endpoint, mirroring the
        /// Stremio catalog extras.
        /// </summary>
        [HttpGet("discover/{kind}")]
        public async Task<IActionResult> Discover(string kind, AnimeService service = AnimeService.Anilist,
            string skip = null, string genre = null, string sort = null)
        {
            try
            {
                var listType = kind?.ToLowerInvariant() switch
                {
                    "trending" => ListType.Trending_Desc,
                    "seasonal" => ListType.Seasonal,
                    "airing" => ListType.Airing,
                    _ => (ListType?)null,
                };
                if (!listType.HasValue) return BadRequest(new { error = "kind must be trending|seasonal|airing" });

                var metas = service switch
                {
                    AnimeService.Kitsu => await _kitsuService.GetAnimeListAsync(null, listType.Value, skip, genre: genre, sort: sort),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(null, listType.Value, skip, genre: genre, sort: sort),
                    _ => await _anilistService.GetAnimeListAsync(null, listType.Value, skip, genre: genre, sort: sort),
                };
                return new JsonResult(new { results = metas });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Discover failed (kind={Kind}, service={Service}).", kind, service);
                return StatusCode(500, new { error = "discover failed" });
            }
        }

        /// <summary>
        /// AniSkip intro/outro/recap markers for one episode. Takes a raw MAL id (numeric)
        /// because that's the key AniSkip uses internally.
        /// </summary>
        [HttpGet("skip/{malId:int}/{episode:int}")]
        public async Task<IActionResult> Skip(int malId, int episode)
        {
            try
            {
                var markers = await _aniSkipService.GetSkipTimesAsync(malId, episode);
                return new JsonResult(new { markers });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Skip failed (malId={MalId}, episode={Episode}).", malId, episode);
                return StatusCode(500, new { error = "skip lookup failed" });
            }
        }

        /// <summary>
        /// AnimeFillerList episode categorisation by show title. Returns a map of
        /// episode-number → category (canon / filler / mixed). Negative-cached upstream
        /// so unknown shows respond fast.
        /// </summary>
        [HttpGet("filler/{*title}")]
        public async Task<IActionResult> Filler(string title)
        {
            try
            {
                var categories = await _fillerListService.GetEpisodeCategoriesAsync(title);
                return new JsonResult(new { categories });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Filler failed (title={Title}).", title);
                return StatusCode(500, new { error = "filler lookup failed" });
            }
        }
    }
}
