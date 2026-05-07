using AnimeList.Models;
using AnimeList.Models.Api;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    [EnableRateLimiting("api")]
    [Tags("Public")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status500InternalServerError)]
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
        [ProducesResponseType(typeof(MappingListResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Mappings(string id)
        {
            try
            {
                List<AnimeIdMapping> entries;
                if (id.StartsWith(imdbPrefix))      entries = await _mappingService.GetImdbMapping(id);
                else if (id.StartsWith(tmdbPrefix)) entries = await _mappingService.GetTmdbMapping(id);
                else
                {
                    AnimeIdMapping single = null;
                    if (id.StartsWith(malPrefix))           single = await _mappingService.GetMalMapping(id);
                    else if (id.StartsWith(kitsuPrefix))    single = await _mappingService.GetKitsuMapping(id);
                    else if (id.StartsWith(anilistPrefix))  single = await _mappingService.GetAnilistMapping(id);
                    entries = single != null ? [single] : [];
                }

                if (entries == null || entries.Count == 0)
                    return NotFound(new ApiError("no mapping for id"));

                return new JsonResult(new MappingListResponse(entries));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Mappings failed (id={Id}).", id);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Unified anime detail. The id's prefix selects the upstream provider — IMDb /
        /// TMDB hit Cinemeta and TMDB respectively; <c>mal:</c> / <c>kitsu:</c> /
        /// <c>anilist:</c> hit each provider's API and enrich the videos array with
        /// Cinemeta data when an IMDb mapping is available.
        /// </summary>
        [HttpGet("anime/{id}")]
        [ProducesResponseType(typeof(AnimeResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Anime(string id)
        {
            try
            {
                if (id.StartsWith(imdbPrefix))
                {
                    var raw = await _cinemetaService.GetAnimeByIdAsync(null, id);
                    return raw == null
                        ? NotFound(new ApiError("not found on cinemeta"))
                        : Content(raw, "application/json");
                }

                Meta meta = null;
                if (id.StartsWith(malPrefix))           meta = await _malService.GetAnimeByIdAsync(id, null);
                else if (id.StartsWith(kitsuPrefix))    meta = await _kitsuService.GetAnimeByIdAsync(id, null);
                else if (id.StartsWith(anilistPrefix))  meta = await _anilistService.GetAnimeByIdAsync(id, null);
                else if (id.StartsWith(tmdbPrefix))     meta = await _tmdbService.GetAnimeByIdAsync(id, null);

                if (meta == null) return NotFound(new ApiError("not found"));
                return new JsonResult(new AnimeResponse(meta));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Anime failed (id={Id}).", id);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// "Audience also liked" recommendations. Powered by AniList anonymously and
        /// translated back to the requested target service so the returned ids are
        /// usable by the caller. Defaults to AniList ids.
        /// </summary>
        [HttpGet("anime/{id}/similar")]
        [ProducesResponseType(typeof(SimilarResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Similar(string id, AnimeService service = AnimeService.Anilist)
        {
            try
            {
                var anilistRaw = await _mappingService.GetIdByService(id, AnimeService.Anilist);
                if (!int.TryParse(anilistRaw, out var anilistId))
                    return NotFound(new ApiError("no anilist mapping for id"));

                var links = await _anilistFallback.GetRecommendationsAsync(anilistId, service);
                return new JsonResult(new SimilarResponse(links));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Similar failed (id={Id}, service={Service}).", id, service);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Streaming destinations (Crunchyroll, Netflix, HiDive, …). MAL has no native
        /// streaming-link field, so for <c>mal:</c> ids we fall through to AniList via
        /// the cross-service mapping — same behaviour as the Stremio "External services"
        /// stream button.
        /// </summary>
        [HttpGet("anime/{id}/streams")]
        [ProducesResponseType(typeof(StreamsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
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
                        return NotFound(new ApiError("no anilist mapping for id"));
                    links = await _anilistFallback.GetExternalLinksAsync(anilistId);
                }

                return new JsonResult(new StreamsResponse(links ?? []));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Streams failed (id={Id}).", id);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Unified search across providers. The <paramref name="service"/> parameter picks
        /// which provider's search endpoint to query (default: AniList for the broadest
        /// coverage). Returns the same Meta shape the catalog endpoints emit.
        /// </summary>
        [HttpGet("search")]
        [ProducesResponseType(typeof(MetaListResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Search(string q, AnimeService service = AnimeService.Anilist, string skip = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(q))
                    return new JsonResult(new MetaListResponse([]));

                var metas = service switch
                {
                    AnimeService.Kitsu => await _kitsuService.GetAnimeListAsync(null, ListType.Search, skip, search: q),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(null, ListType.Search, skip, search: q),
                    _ => await _anilistService.GetAnimeListAsync(null, ListType.Search, skip, search: q),
                };
                return new JsonResult(new MetaListResponse(metas));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Search failed (q={Q}, service={Service}).", q, service);
                return StatusCode(500, new ApiError("search failed"));
            }
        }

        /// <summary>
        /// Best-match resolver for fuzzy / page-scraped titles. Hits the upstream
        /// search endpoint, normalises every result's title (strips year tags, parens
        /// content, "Season N" / "Part N" suffixes, punctuation) and scores them by
        /// Jaccard token overlap against the normalised query. Returns the top
        /// <paramref name="limit"/> results sorted by score, with the cross-service
        /// mapping attached so a tracker doesn't need a second round-trip to learn
        /// the equivalent ids on other providers.
        ///
        /// Designed for browser extensions / Plex webhooks / Discord bots that have
        /// a free-text show name and want a confident anime id back.
        /// </summary>
        /// <param name="title">The show title. Page-scraped strings with dub/sub
        /// suffixes, year tags or romanised variants are fine — the scorer
        /// normalises them.</param>
        /// <param name="service">Which provider's search to query. AniList default;
        /// pick MyAnimeList or Kitsu only if you have a strong reason.</param>
        /// <param name="limit">Maximum number of ranked matches to return. Defaults
        /// to 5; capped at 20.</param>
        [HttpGet("match")]
        [ProducesResponseType(typeof(MatchResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Match(string title, AnimeService service = AnimeService.Anilist, int limit = 5)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title))
                    return BadRequest(new ApiError("title is required"));

                limit = Math.Clamp(limit, 1, 20);
                var normalisedQuery = NormalizeTitle(title);

                // groupSeasons=false forces the catalog to emit service-native ids
                // (anilist:N / kitsu:N / mal:N) instead of collapsing to IMDb / TMDB.
                // Downstream callers — including the browser-extension auto-tracker —
                // use the returned id with /entries/{id} which expects the provider's
                // native id space, so handing back an IMDb id would 400 on save.
                var raw = service switch
                {
                    AnimeService.Kitsu => await _kitsuService.GetAnimeListAsync(null, ListType.Search, search: title, groupSeasons: false),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(null, ListType.Search, search: title, groupSeasons: false),
                    _ => await _anilistService.GetAnimeListAsync(null, ListType.Search, search: title, groupSeasons: false),
                };

                var ranked = raw
                    .Select(m => new { meta = m, score = ScoreMatch(normalisedQuery, m.name) })
                    .OrderByDescending(x => x.score)
                    .Take(limit)
                    .ToList();

                // Build the response with cross-service mappings attached so the caller
                // can pick whichever id its tracker primary uses without a second hit.
                var matches = new List<object>();
                foreach (var r in ranked)
                {
                    AnimeIdMapping mapping = null;
                    if (!string.IsNullOrEmpty(r.meta.id))
                    {
                        if (r.meta.id.StartsWith(anilistPrefix))      mapping = await _mappingService.GetAnilistMapping(r.meta.id);
                        else if (r.meta.id.StartsWith(kitsuPrefix))   mapping = await _mappingService.GetKitsuMapping(r.meta.id);
                        else if (r.meta.id.StartsWith(malPrefix))     mapping = await _mappingService.GetMalMapping(r.meta.id);
                    }

                    matches.Add(new
                    {
                        id = r.meta.id,
                        name = r.meta.name,
                        poster = r.meta.poster,
                        score = Math.Round(r.score, 3),
                        mapping,
                    });
                }

                return new JsonResult(new
                {
                    query = title,
                    normalised = normalisedQuery,
                    matches,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Match failed (title={Title}, service={Service}).", title, service);
                return StatusCode(500, new ApiError("match failed"));
            }
        }

        /// <summary>
        /// Discovery catalogs: <c>trending</c>, <c>seasonal</c>, <c>airing</c>. The
        /// <paramref name="genre"/> param doubles as the season selector ("This Season",
        /// "Next Season", "Previous Season") for the seasonal endpoint, mirroring the
        /// Stremio catalog extras.
        /// </summary>
        [HttpGet("discover/{kind}")]
        [ProducesResponseType(typeof(MetaListResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
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
                if (!listType.HasValue) return BadRequest(new ApiError("kind must be trending|seasonal|airing"));

                var metas = service switch
                {
                    AnimeService.Kitsu => await _kitsuService.GetAnimeListAsync(null, listType.Value, skip, genre: genre, sort: sort),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(null, listType.Value, skip, genre: genre, sort: sort),
                    _ => await _anilistService.GetAnimeListAsync(null, listType.Value, skip, genre: genre, sort: sort),
                };
                return new JsonResult(new MetaListResponse(metas));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Discover failed (kind={Kind}, service={Service}).", kind, service);
                return StatusCode(500, new ApiError("discover failed"));
            }
        }

        /// <summary>
        /// AniSkip intro/outro/recap markers for one episode. Accepts any prefixed id
        /// (<c>mal:N</c>, <c>anilist:N</c>, <c>kitsu:N</c>, <c>tt…</c>, <c>tmdb:N</c>)
        /// or a raw numeric MAL id; non-MAL ids are translated through the cross-
        /// service mapping. Returns an empty list when AniSkip has no markers for
        /// the resolved id, the upstream is down, or the id can't be resolved to MAL.
        /// </summary>
        [HttpGet("skip/{id}/{episode:int}")]
        [ProducesResponseType(typeof(SkipResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Skip(string id, int episode)
        {
            try
            {
                var malId = await ResolveMalIdAsync(id);
                if (malId is null or <= 0)
                    return NotFound(new ApiError("no MAL mapping for id"));

                var markers = await _aniSkipService.GetSkipTimesAsync(malId.Value, episode);
                return new JsonResult(new SkipResponse(markers));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Skip failed (id={Id}, episode={Episode}).", id, episode);
                return StatusCode(500, new ApiError("skip lookup failed"));
            }
        }

        /// <summary>
        /// AnimeFillerList episode categorisation. Accepts either a show title or a
        /// prefixed id (<c>mal:N</c>, <c>anilist:N</c>, <c>kitsu:N</c>, <c>tt…</c>,
        /// <c>tmdb:N</c>); ids are resolved to the title via the cross-service mapping
        /// before the AFL slug lookup. Returns a map of episode-number → category
        /// (canon / filler / mixed). Negative-cached upstream so unknown shows respond
        /// fast.
        /// </summary>
        [HttpGet("filler/{*titleOrId}")]
        [ProducesResponseType(typeof(FillerResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Filler(string titleOrId)
        {
            try
            {
                var title = LooksLikeId(titleOrId)
                    ? await ResolveTitleAsync(titleOrId)
                    : titleOrId;

                if (string.IsNullOrEmpty(title))
                    return NotFound(new ApiError("could not resolve a title for the id"));

                var categories = await _fillerListService.GetEpisodeCategoriesAsync(title);
                return new JsonResult(new FillerResponse(title, categories));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Filler failed (titleOrId={TitleOrId}).", titleOrId);
                return StatusCode(500, new ApiError("filler lookup failed"));
            }
        }

        // Resolves any prefixed id (or a raw MAL integer) to a numeric MAL id.
        private async Task<int?> ResolveMalIdAsync(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            // Bare numeric: treat as a MAL id directly so existing callers that hit
            // /skip/{malId}/{episode} keep working without re-shaping their URLs.
            if (int.TryParse(id, out var direct)) return direct;

            var raw = await _mappingService.GetIdByService(id, AnimeService.MyAnimeList);
            return int.TryParse(raw, out var resolved) ? resolved : null;
        }

        // Looks up the show's canonical title. Tries the prefix's native service
        // first; for IMDb / TMDB ids walks the mapping until one provider has a
        // summary. Returns null when nothing in the chain knows the show.
        private async Task<string> ResolveTitleAsync(string id)
        {
            if (id.StartsWith(malPrefix))
                return (await _malService.GetAnimeSummaryAsync(id)).name;
            if (id.StartsWith(anilistPrefix))
                return (await _anilistService.GetAnimeSummaryAsync(id)).name;
            if (id.StartsWith(kitsuPrefix))
                return (await _kitsuService.GetAnimeSummaryAsync(id)).name;

            foreach (var (svc, prefix, summary) in new (AnimeService, string, Func<string, Task<(string name, int? episodeCount)>>)[]
            {
                (AnimeService.MyAnimeList, malPrefix, _malService.GetAnimeSummaryAsync),
                (AnimeService.Anilist, anilistPrefix, _anilistService.GetAnimeSummaryAsync),
                (AnimeService.Kitsu, kitsuPrefix, _kitsuService.GetAnimeSummaryAsync),
            })
            {
                var raw = await _mappingService.GetIdByService(id, svc);
                if (string.IsNullOrEmpty(raw)) continue;
                var name = (await summary($"{prefix}{raw}")).name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return null;
        }

        private static bool LooksLikeId(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.StartsWith(imdbPrefix)
                || s.StartsWith(malPrefix)
                || s.StartsWith(anilistPrefix)
                || s.StartsWith(kitsuPrefix)
                || s.StartsWith(tmdbPrefix);
        }

        // NormalizeTitle and ScoreMatch live in Utils.cs (globally usable via the
        // `using static AnimeList.Utils` import) — shared with CatalogController so
        // the Stremio search ranking and the API /match scoring stay in step.
    }
}
