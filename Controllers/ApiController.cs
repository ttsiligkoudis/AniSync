using AnimeList.Models;
using AnimeList.Models.Api;
using AnimeList.Services.Extensions;
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
    // Anonymous read API: no cookie auth, no per-user state, no CSRF surface. Documented
    // surface for non-Stremio clients (scripting, SDK generators), which can't be expected
    // to mint an antiforgery token.
    [IgnoreAntiforgeryToken]
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
        private readonly ISubtitleService _subtitleService;
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;
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
            ISubtitleService subtitleService,
            ITokenService tokenService,
            IConfigStore configStore,
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
            _subtitleService = subtitleService;
            _tokenService = tokenService;
            _configStore = configStore;
            _logger = logger;
        }

        // True when the caller's session has explicitly opted in to 18+
        // content via /configure → "Show 18+ content". Anonymous viewers
        // (no session), unsigned-in browsers, and external programmatic
        // callers all default to family-safe (hideAdult = true) since
        // they have no Configuration to set the bit. Used by the search
        // / match / catalog endpoints below so the same toggle that
        // governs /discover and /library also governs the public site
        // surface that hits this controller.
        private async Task<bool> ResolveHideAdultAsync()
        {
            try
            {
                var (_, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
                if (string.IsNullOrEmpty(uid)) return true;
                var cfg = await GetConfigByUidAsync(uid, _configStore);
                return cfg?.showAdultContent != true;
            }
            catch
            {
                // Session corrupt / store hiccup — fall back to safe.
                return true;
            }
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

                // groupSeasons=false: the /anime/{id}/similar API is the
                // raw per-service shape — callers that want imdb-grouped
                // ids do their own ApplyGroupingToMetasAsync pass.
                var links = await _anilistFallback.GetRecommendationsAsync(anilistId, service, groupSeasons: false);
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
            var hideAdult = await ResolveHideAdultAsync();
            try
            {
                if (string.IsNullOrWhiteSpace(q))
                    return new JsonResult(new MetaListResponse([]));

                // Outage fallback: caller picked (or defaulted to) AniList but the
                // upstream is down — re-route the search through Kitsu so the
                // header search-as-you-type doesn't go silent for users on AniList.
                var effectiveService = service;
                if (effectiveService == AnimeService.Anilist && AnimeList.Services.AnilistHealthMonitor.IsDown)
                    effectiveService = AnimeService.Kitsu;
                var metas = effectiveService switch
                {
                    AnimeService.Kitsu => await _kitsuService.GetAnimeListAsync(null, ListType.Search, skip, search: q, hideAdult: hideAdult),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(null, ListType.Search, skip, search: q, hideAdult: hideAdult),
                    _ => await _anilistService.GetAnimeListAsync(null, ListType.Search, skip, search: q, hideAdult: hideAdult),
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
        /// <param name="service">Which provider's search to query. When unspecified,
        /// resolves to the caller's session primary (so the in-app site search returns
        /// ids in the user's own id-space) and falls through to AniList for callers
        /// without a session — browser extensions, webhooks, bots.</param>
        /// <param name="limit">Maximum number of ranked matches to return. Defaults
        /// to 5; capped at 20.</param>
        [HttpGet("match")]
        [ProducesResponseType(typeof(MatchResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Match(string title, AnimeService? service = null, int limit = 5)
        {
            var hideAdult = await ResolveHideAdultAsync();
            try
            {
                if (string.IsNullOrWhiteSpace(title))
                    return BadRequest(new ApiError("title is required"));

                limit = Math.Clamp(limit, 1, 20);
                var normalisedQuery = NormalizeTitle(title);

                // When the caller didn't pick a service explicitly, default to
                // their session's primary so the in-app site search returns ids
                // in the user's own id-space — clicking a Kitsu user's result
                // should land on /anime/kitsu:N, not /anime/anilist:N. Callers
                // without a session (the API's primary external audience —
                // extensions / bots / webhooks) fall through to AniList.
                var requestedService = service;
                if (!requestedService.HasValue)
                {
                    try
                    {
                        var (token, _) = await _tokenService.ResolveCurrentAsync(_configStore);
                        if (token != null) requestedService = token.anime_service;
                    }
                    catch { /* session corrupt — fall through to AniList */ }
                }

                // groupSeasons=false forces the catalog to emit service-native ids
                // (anilist:N / kitsu:N / mal:N) instead of collapsing to IMDb / TMDB.
                // Downstream callers — including the browser-extension auto-tracker —
                // use the returned id with /entries/{id} which expects the provider's
                // native id space, so handing back an IMDb id would 400 on save.
                // Same outage fallback as /search — Kitsu when AniList is down,
                // so the global header search-as-you-type stays useful.
                var effectiveService = requestedService ?? AnimeService.Anilist;
                if (effectiveService == AnimeService.Anilist && AnimeList.Services.AnilistHealthMonitor.IsDown)
                    effectiveService = AnimeService.Kitsu;
                var raw = effectiveService switch
                {
                    AnimeService.Kitsu => await _kitsuService.GetAnimeListAsync(null, ListType.Search, search: title, groupSeasons: false, hideAdult: hideAdult),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(null, ListType.Search, search: title, groupSeasons: false, hideAdult: hideAdult),
                    _ => await _anilistService.GetAnimeListAsync(null, ListType.Search, search: title, groupSeasons: false, hideAdult: hideAdult),
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
                        type = r.meta.type,
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
        /// Header typeahead search spanning the user's enabled media types: anime
        /// (their primary tracker, like <see cref="Match"/>) plus Trakt/Cinemeta
        /// movies and series. Results are merged by relevance. Trakt carries anime
        /// too, so a video result whose IMDb id matches one of the anime hits is
        /// dropped to avoid a duplicate row. Each match returns an id ready for the
        /// /meta route (service-native for anime, tt for movies/series) plus its
        /// type so the client can append ?type= for video.
        /// </summary>
        [HttpGet("suggest")]
        public async Task<IActionResult> Suggest(string title, int limit = 8)
        {
            if (string.IsNullOrWhiteSpace(title))
                return new JsonResult(new { query = title, matches = Array.Empty<object>() });

            limit = Math.Clamp(limit, 1, 20);
            var normalised = NormalizeTitle(title);
            var hideAdult = await ResolveHideAdultAsync();

            string uid = null;
            var primary = AnimeService.Anilist;
            try
            {
                var (token, resolvedUid) = await _tokenService.ResolveCurrentAsync(_configStore);
                if (token != null) primary = token.anime_service;
                uid = resolvedUid;
            }
            catch { /* no or corrupt session — fall through to defaults */ }

            var enabled = await AnimeList.Services.MediaTypePreference.ResolveEnabledAsync(HttpContext, uid, _configStore);

            // (id, name, poster, type, score). Built per source, merged at the end.
            var scored = new List<(string Id, string Name, string Poster, string Type, double Score)>();
            // IMDb ids already represented by an anime hit — used to drop duplicate
            // video rows for the same title (Trakt indexes anime as well).
            var animeImdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (enabled.Contains(MetaType.anime))
                {
                    var svc = primary;
                    if (svc == AnimeService.Anilist && AnimeList.Services.AnilistHealthMonitor.IsDown)
                        svc = AnimeService.Kitsu;
                    var raw = svc switch
                    {
                        AnimeService.Kitsu => await _kitsuService.GetAnimeListAsync(null, ListType.Search, search: title, groupSeasons: false, hideAdult: hideAdult),
                        AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(null, ListType.Search, search: title, groupSeasons: false, hideAdult: hideAdult),
                        _ => await _anilistService.GetAnimeListAsync(null, ListType.Search, search: title, groupSeasons: false, hideAdult: hideAdult),
                    };
                    foreach (var m in raw.OrderByDescending(m => ScoreMatch(normalised, m.name)).Take(limit))
                    {
                        if (string.IsNullOrEmpty(m.id)) continue;
                        try
                        {
                            AnimeIdMapping map = null;
                            if (m.id.StartsWith(anilistPrefix)) map = await _mappingService.GetAnilistMapping(m.id);
                            else if (m.id.StartsWith(kitsuPrefix)) map = await _mappingService.GetKitsuMapping(m.id);
                            else if (m.id.StartsWith(malPrefix)) map = await _mappingService.GetMalMapping(m.id);
                            if (!string.IsNullOrEmpty(map?.ImdbId)) animeImdbIds.Add(map.ImdbId);
                        }
                        catch { /* mapping miss — still show the anime result */ }
                        scored.Add((m.id, m.name, m.poster, m.type ?? "anime", ScoreMatch(normalised, m.name)));
                    }
                }

                foreach (var vt in new[] { MetaType.movie, MetaType.series })
                {
                    if (!enabled.Contains(vt)) continue;
                    var t = vt == MetaType.movie ? "movie" : "series";
                    var vids = await _cinemetaService.GetVideoCatalogAsync(t, search: title);
                    foreach (var m in vids.OrderByDescending(m => ScoreMatch(normalised, m.name)).Take(limit))
                    {
                        if (string.IsNullOrEmpty(m.id)) continue;
                        // Same title already covered by an anime hit → skip the dup.
                        if (animeImdbIds.Contains(m.id)) continue;
                        scored.Add((m.id, m.name, m.poster, t, ScoreMatch(normalised, m.name)));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "API Suggest failed (title={Title}).", title);
            }

            var matches = scored
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .Select(x => (object)new { id = x.Id, name = x.Name, poster = x.Poster, type = x.Type })
                .ToList();

            return new JsonResult(new { query = title, matches });
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
            var hideAdult = await ResolveHideAdultAsync();
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
                    AnimeService.Kitsu => await _kitsuService.GetAnimeListAsync(null, listType.Value, skip, genre: genre, sort: sort, hideAdult: hideAdult),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(null, listType.Value, skip, genre: genre, sort: sort, hideAdult: hideAdult),
                    _ => await _anilistService.GetAnimeListAsync(null, listType.Value, skip, genre: genre, sort: sort, hideAdult: hideAdult),
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

        // ── Airing schedule ─────────────────────────────────────────────────────

        /// <summary>
        /// Anime with an episode airing during the calendar day in the
        /// caller's timezone. One row per anime (cours overlapping in
        /// the same day collapse to a single entry). Same data the
        /// dashboard's "New Episodes Today" shelf renders.
        /// </summary>
        /// <param name="service">The anime service to query.</param>
        /// <param name="tz">Caller's UTC offset in minutes, JS
        /// <c>Date.getTimezoneOffset()</c> convention — positive = west
        /// of UTC. Defaults to 0 (UTC day) when omitted.</param>
        [HttpGet("airing/today")]
        [ProducesResponseType(typeof(AiringTodayResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> AiringToday(AnimeService service = AnimeService.Anilist, int tz = 0)
        {
            try
            {
                // Clamp the same range JS getTimezoneOffset emits so a
                // malformed query string can't make the cache key absurd.
                var offset = Math.Clamp(tz, -840, 720);
                var items = await _anilistFallback.GetNewEpisodesTodayAsync(service, offset);
                return new JsonResult(new AiringTodayResponse(items ?? []));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API AiringToday failed (service={Service}, tz={Tz}).", service, tz);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Episodes airing in an arbitrary window (<paramref name="hours"/> from now,
        /// up to 168 / one week). Same source the episode-notification scheduler
        /// uses to arm its per-episode timers. Not cached — caller's cadence is the
        /// source of throttling.
        /// </summary>
        /// <param name="hours">Lookahead window in hours. 1–168 (one week). Defaults to 24.</param>
        [HttpGet("airing/upcoming")]
        [ProducesResponseType(typeof(AiringUpcomingResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> AiringUpcoming(int hours = 24)
        {
            if (hours < 1 || hours > 168)
                return BadRequest(new ApiError("hours must be between 1 and 168"));
            try
            {
                var now = DateTimeOffset.UtcNow;
                var start = now.AddHours(-1).ToUnixTimeSeconds();
                var end = now.AddHours(hours).ToUnixTimeSeconds();
                var items = await _anilistFallback.GetUpcomingEpisodesAsync(start, end);
                return new JsonResult(new AiringUpcomingResponse(start, end, items ?? []));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API AiringUpcoming failed (hours={Hours}).", hours);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        // ── Per-anime supplementary data ────────────────────────────────────────

        /// <summary>
        /// Episode list extracted from the anime's full meta. Same data
        /// <c>/api/v1/anime/{id}</c> embeds inside the videos array, surfaced as a
        /// dedicated sub-resource so a client can refresh per-episode metadata
        /// (filler markers, thumbnails, air dates) without re-fetching the show
        /// envelope.
        /// </summary>
        [HttpGet("anime/{id}/episodes")]
        [ProducesResponseType(typeof(EpisodesResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Episodes(string id)
        {
            try
            {
                Meta meta = null;
                if (id.StartsWith(malPrefix))           meta = await _malService.GetAnimeByIdAsync(id, null);
                else if (id.StartsWith(kitsuPrefix))    meta = await _kitsuService.GetAnimeByIdAsync(id, null);
                else if (id.StartsWith(anilistPrefix))  meta = await _anilistService.GetAnimeByIdAsync(id, null);
                else if (id.StartsWith(tmdbPrefix))     meta = await _tmdbService.GetAnimeByIdAsync(id, null);

                if (meta?.videos == null) return NotFound(new ApiError("not found"));
                var rows = meta.videos.Select(v => new EpisodeInfo(
                    Season: v.season,
                    Episode: v.episode,
                    Title: v.title ?? v.name,
                    Thumbnail: v.thumbnail,
                    Released: v.released ?? v.firstAired,
                    Overview: v.overview ?? v.description)).ToList();
                return new JsonResult(new EpisodesResponse(meta.id ?? id, rows));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Episodes failed (id={Id}).", id);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// YouTube trailer id for an anime, or null when no trailer is published or
        /// it's hosted on a non-YouTube platform. Translates the id to AniList via
        /// the mapping service so the call works for mal: / kitsu: / imdb: / tmdb:
        /// ids too.
        /// </summary>
        [HttpGet("anime/{id}/trailer")]
        [ProducesResponseType(typeof(TrailerResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Trailer(string id)
        {
            try
            {
                var anilistRaw = await _mappingService.GetIdByService(id, AnimeService.Anilist);
                if (!int.TryParse(anilistRaw, out var anilistId))
                    return NotFound(new ApiError("no anilist mapping for id"));
                var youtubeId = await _anilistFallback.GetYoutubeTrailerIdAsync(anilistId);
                return new JsonResult(new TrailerResponse(youtubeId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Trailer failed (id={Id}).", id);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Prequels and sequels for an anime, sorted chronologically (story-order
        /// approximation). Returns slim Meta entries; ids translated to
        /// <paramref name="service"/>'s native space when a mapping exists.
        /// </summary>
        [HttpGet("anime/{id}/related")]
        [ProducesResponseType(typeof(MetaListResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Related(string id, AnimeService service = AnimeService.Anilist)
        {
            try
            {
                var anilistRaw = await _mappingService.GetIdByService(id, AnimeService.Anilist);
                if (!int.TryParse(anilistRaw, out var anilistId))
                    return NotFound(new ApiError("no anilist mapping for id"));
                var items = await _anilistFallback.GetRelatedAsync(anilistId, service);
                return new JsonResult(new MetaListResponse(items ?? []));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Related failed (id={Id}, service={Service}).", id, service);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Recommendation carousel — same data the detail page's "You might also like"
        /// shelf renders. Richer than <c>/anime/{id}/similar</c> (which returns
        /// minimal Link entries): includes posters, scores, episode counts, format.
        /// </summary>
        [HttpGet("anime/{id}/recommendations")]
        [ProducesResponseType(typeof(MetaListResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Recommendations(string id, AnimeService service = AnimeService.Anilist)
        {
            try
            {
                var anilistRaw = await _mappingService.GetIdByService(id, AnimeService.Anilist);
                if (!int.TryParse(anilistRaw, out var anilistId))
                    return NotFound(new ApiError("no anilist mapping for id"));
                var items = await _anilistFallback.GetRecommendationMetasAsync(anilistId, service);
                return new JsonResult(new MetaListResponse(items ?? []));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Recommendations failed (id={Id}, service={Service}).", id, service);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// AniList-sourced supplementary chips — staff, studios, tags, composer,
        /// producer credits. Used by the detail page to render the metadata sidebar
        /// for anime whose primary tracker (MAL / Kitsu) doesn't surface this depth.
        /// </summary>
        [HttpGet("anime/{id}/supplementary")]
        [ProducesResponseType(typeof(SupplementaryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Supplementary(string id)
        {
            try
            {
                var anilistRaw = await _mappingService.GetIdByService(id, AnimeService.Anilist);
                if (!int.TryParse(anilistRaw, out var anilistId))
                    return NotFound(new ApiError("no anilist mapping for id"));
                var links = await _anilistFallback.GetSupplementaryLinksAsync(anilistId);
                return new JsonResult(new SupplementaryResponse(links ?? []));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Supplementary failed (id={Id}).", id);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Cross-service id bundle for an anime — every AniList / MAL / Kitsu /
        /// IMDb / TMDB / TVDB / AniDB id we know about. Used by the detail page's
        /// "open on X" buttons.
        /// </summary>
        [HttpGet("anime/{id}/links")]
        [ProducesResponseType(typeof(SourceLinksResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Links(string id)
        {
            try
            {
                var links = await _mappingService.BuildSourceLinksAsync(id);
                if (links == null) return NotFound(new ApiError("no source links for id"));
                return new JsonResult(new SourceLinksResponse(links));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Links failed (id={Id}).", id);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Subtitle tracks for one episode from OpenSubtitles. Anime without
        /// an IMDb mapping return an empty list. Same source the watch page
        /// and the Stremio SubtitlesController draw from.
        /// </summary>
        /// <param name="id">Service-prefixed anime id.</param>
        /// <param name="episode">Episode number (within the chosen season).</param>
        /// <param name="season">Optional season number for multi-cour franchises. Defaults to 1.</param>
        [HttpGet("anime/{id}/episodes/{episode:int}/subtitles")]
        [ProducesResponseType(typeof(SubtitlesResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Subtitles(string id, int episode, int? season = null)
        {
            try
            {
                var sourceLinks = await _mappingService.BuildSourceLinksAsync(id);
                var imdbId = sourceLinks?.ImdbId;
                if (string.IsNullOrEmpty(imdbId))
                    return new JsonResult(new SubtitlesResponse([], new SubtitleProviderCounts(0)));

                var tracks = await SafeOpenSubtitlesSearch(imdbId, season, episode);
                return new JsonResult(new SubtitlesResponse(
                    tracks.ToList(),
                    new SubtitleProviderCounts(tracks.Count)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Subtitles failed (id={Id}, ep={Ep}).", id, episode);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        private async Task<IReadOnlyList<SubtitleTrack>> SafeOpenSubtitlesSearch(string imdbId, int? season, int episode)
        {
            try { return await _subtitleService.SearchAsync(imdbId, season, episode); }
            catch (Exception ex) { _logger.LogWarning(ex, "OpenSubtitles search failed (imdb={Imdb}, ep={Ep})", imdbId, episode); return []; }
        }

        // ── Season / catalog metadata ───────────────────────────────────────────

        /// <summary>
        /// Current-season aggregate counts — currently airing, new this season,
        /// total this season. Same numbers the dashboard's "This Season" strip
        /// renders; 24h-cached upstream so calling this on a hot loop is fine.
        /// </summary>
        [HttpGet("stats/season")]
        [ProducesResponseType(typeof(SeasonStatsResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> SeasonStats()
        {
            try
            {
                var (airing, newThis, total) = await _anilistFallback.GetSeasonStatsAsync();
                return new JsonResult(new SeasonStatsResponse(airing, newThis, total));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API SeasonStats failed.");
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Full AniList tag catalog (non-adult, grouped by category). Used by the
        /// <c>/discover/tag</c> listing page. 24h-cached upstream.
        /// </summary>
        [HttpGet("tags")]
        [ProducesResponseType(typeof(TagsListResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Tags()
        {
            try
            {
                var tags = await _anilistFallback.GetTagsListAsync();
                return new JsonResult(new TagsListResponse(tags ?? []));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Tags failed.");
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// One page of animation studios sorted by popularity. Each entry
        /// carries its anime count so a UI can render "Studio · N anime" tiles
        /// without a per-studio follow-up. 1-indexed pagination.
        /// </summary>
        [HttpGet("studios")]
        [ProducesResponseType(typeof(StudiosListResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Studios(int page = 1)
        {
            if (page < 1) page = 1;
            try
            {
                var (studios, hasNext) = await _anilistFallback.GetStudiosListAsync(page);
                return new JsonResult(new StudiosListResponse(studios ?? [], hasNext));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Studios failed (page={Page}).", page);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// One page of a studio's catalog (every anime they produced), sorted
        /// alphabetically. 1-indexed pagination. <c>HasNextPage</c> is the
        /// authoritative stop signal — empty pages can precede more pages
        /// because the underlying media list is filtered server-side.
        /// </summary>
        [HttpGet("studios/{studioId:int}/anime")]
        [ProducesResponseType(typeof(StudioMediaResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> StudioMedia(int studioId, AnimeService service = AnimeService.Anilist, int page = 1)
        {
            if (page < 1) page = 1;
            try
            {
                var hideAdult = await ResolveHideAdultAsync();
                var (name, items, hasNext) = await _anilistFallback.GetStudioMediaAsync(studioId, service, page, hideAdult);
                return new JsonResult(new StudioMediaResponse(name, items ?? [], hasNext));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API StudioMedia failed (studio={Id}, page={Page}).", studioId, page);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// A staff member's filmography sorted by popularity. <paramref name="skip"/>
        /// is the opaque pagination cursor (number of cards already returned).
        /// </summary>
        [HttpGet("staff/{staffId:int}/anime")]
        [ProducesResponseType(typeof(StaffMediaResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> StaffMedia(int staffId, AnimeService service = AnimeService.Anilist, string skip = null)
        {
            try
            {
                var hideAdult = await ResolveHideAdultAsync();
                var (name, items) = await _anilistFallback.GetStaffMediaAsync(staffId, service, skip, hideAdult);
                return new JsonResult(new StaffMediaResponse(name, items ?? []));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API StaffMedia failed (staff={Id}, skip={Skip}).", staffId, skip);
                return StatusCode(500, new ApiError("lookup failed"));
            }
        }

        /// <summary>
        /// Browse every anime tagged with <paramref name="tag"/>, sorted by
        /// popularity. 1-indexed pagination; <c>HasNextPage</c> is the stop
        /// signal.
        /// </summary>
        [HttpGet("discover/by-tag/{tag}")]
        [ProducesResponseType(typeof(TagMediaResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> DiscoverByTag(string tag, AnimeService service = AnimeService.Anilist, int page = 1)
        {
            if (page < 1) page = 1;
            try
            {
                var hideAdult = await ResolveHideAdultAsync();
                var (items, hasNext) = await _anilistFallback.GetByTagPageAsync(tag, service, page, hideAdult);
                return new JsonResult(new TagMediaResponse(tag, items ?? [], hasNext));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API DiscoverByTag failed (tag={Tag}, page={Page}).", tag, page);
                return StatusCode(500, new ApiError("lookup failed"));
            }
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
