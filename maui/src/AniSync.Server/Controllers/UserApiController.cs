using AnimeList.Filters;
using AnimeList.Models;
using AnimeList.Models.Api;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnimeList.Controllers
{
    /// <summary>
    /// User-scoped API surface for non-Stremio clients.
    ///
    /// <para><b>Authentication.</b> Every endpoint requires the Config UID via the
    /// <c>X-AniSync-Config</c> request header. The UID never appears in the URL
    /// — that's deliberate, so it can't leak through Referer, reverse-proxy /
    /// CDN access logs, browser history, or shared screenshots. Stremio's addon
    /// protocol embeds the UID in the path because it has no other transport;
    /// those routes live in <see cref="CatalogController"/>, <see cref="MetaController"/>,
    /// <see cref="StreamController"/>, <see cref="SubtitlesController"/>, and
    /// <see cref="ManifestController"/> and are not part of <c>/api/v1</c>.</para>
    ///
    /// <para>Treat the UID like a password. If it leaks (forum post, screenshot,
    /// log dump) sign out and re-link from the configure page to mint a fresh
    /// one and invalidate the old.</para>
    /// </summary>
    [ApiController]
    [Route("api/v1/me")]
    [EnableRateLimiting("api")]
    [Tags("User-scoped")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status500InternalServerError)]
    // X-AniSync-Config (the per-user UID) is the auth — not the session cookie — so
    // CSRF doesn't apply: a third party with the UID is authorised by design (SDK
    // generators, browser extension MV3 background, external scripting). The cookie-
    // based site UI uses /api/library/* (MVC) routes that DO go through the filter.
    [IgnoreAntiforgeryToken]
    public class UserApiController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly ITraktService _traktService;
        private readonly ICinemetaService _cinemeta;
        private readonly IConfigStore _configStore;
        private readonly ISyncService _syncService;
        private readonly IAnimeMappingService _mappingService;
        private readonly IUserListCache _listCache;
        private readonly IAnimeScheduleService _scheduleService;
        private readonly IWatchingCacheStore _watchingCache;
        private readonly IHiddenEntryStore _hiddenStore;
        private readonly IMergedListService _mergedListService;
        private readonly IAddonStreamService _addonStreamService;
        private readonly IAniSkipService _aniSkipService;
        private readonly ISubtitleService _subtitleService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<UserApiController> _logger;

        public UserApiController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            ITraktService traktService,
            ICinemetaService cinemeta,
            IConfigStore configStore,
            ISyncService syncService,
            IAnimeMappingService mappingService,
            IUserListCache listCache,
            IAnimeScheduleService scheduleService,
            IWatchingCacheStore watchingCache,
            IHiddenEntryStore hiddenStore,
            IMergedListService mergedListService,
            IAddonStreamService addonStreamService,
            IAniSkipService aniSkipService,
            ISubtitleService subtitleService,
            IHttpClientFactory httpClientFactory,
            ILogger<UserApiController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _traktService = traktService;
            _cinemeta = cinemeta;
            _configStore = configStore;
            _syncService = syncService;
            _mappingService = mappingService;
            _listCache = listCache;
            _scheduleService = scheduleService;
            _watchingCache = watchingCache;
            _hiddenStore = hiddenStore;
            _mergedListService = mergedListService;
            _addonStreamService = addonStreamService;
            _aniSkipService = aniSkipService;
            _subtitleService = subtitleService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Library export from the primary provider. Returns list entries with
        /// progress, score, notes, dates and a service-prefixed media id. Useful for
        /// backup tooling or migrating to a custom client.
        /// </summary>
        /// <param name="status">Optional list filter. Accepts the friendly names
        /// <c>watching</c> / <c>current</c>, <c>completed</c>, <c>planning</c> /
        /// <c>planned</c> / <c>plan_to_watch</c>, <c>paused</c> / <c>on_hold</c>,
        /// <c>dropped</c>, <c>rewatching</c> / <c>repeating</c>. Aliases are
        /// case-insensitive and normalised internally so the same name works
        /// regardless of which provider is primary. Omit to return every entry.</param>
        [HttpGet("library")]
        [RequireConfig]
        [ProducesResponseType(typeof(LibraryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Library(ListStatus? status = null)
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                var entries = tokenData.anime_service switch
                {
                    AnimeService.Anilist => await _anilistService.GetUserListEntriesAsync(tokenData),
                    AnimeService.MyAnimeList => await _malService.GetUserListEntriesAsync(tokenData),
                    _ => await _kitsuService.GetUserListEntriesAsync(tokenData),
                };

                if (status.HasValue)
                {
                    // Map the canonical enum back to the lowercase string the existing
                    // NormalizeListStatus comparator works on (entry.Status is the
                    // raw provider-side value).
                    var requested = NormalizeListStatus(status.Value.ToString());
                    entries = entries
                        .Where(e => NormalizeListStatus(e.Status) == requested)
                        .ToList();
                }

                return new JsonResult(new LibraryResponse(
                    Primary: tokenData.anime_service.ToString(),
                    Status: status?.ToString().ToLowerInvariant(),
                    Entries: entries));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Library failed (status={Status}).", status);
                return StatusCode(500, new ApiError("library lookup failed"));
            }
        }

        // NormalizeListStatus lives in Utils and is globally usable via the
        // `using static AnimeList.Utils` import — see Utils.cs for the mapping.

        /// <summary>
        /// One list entry by media id. Returns status / progress / score / notes /
        /// rewatch count / start &amp; finish dates / total episodes for the primary
        /// provider's view of this anime.
        ///
        /// <para>For a franchise reached via a cross-service id (imdb:/tmdb:) split across
        /// several provider anime, the response also carries the per-cour <c>Seasons</c>
        /// list, the auto-resolved <c>SelectedEntryId</c> the entry was read against, and
        /// the primary <c>Service</c> (so the Manage Entry modal can render the Season
        /// dropdown and pick the right score range). The modal refetches a specific cour
        /// by calling this endpoint again with that cour's id as <paramref name="id"/>.
        /// Ports the MVC MetaController.GetEntryByApi shape.</para>
        /// </summary>
        /// <param name="id">Service-prefixed media id (<c>anilist:N</c>, <c>kitsu:N</c>,
        /// <c>mal:N</c>) or a cross-service id (<c>tt…</c> / <c>tmdb:N</c>) for a franchise.</param>
        /// <param name="season">Optional cour / season number — forwarded to the provider's
        /// per-cour entry read for native ids.</param>
        /// <param name="type">"movie" / "series" when the Manage Entry modal was opened from a
        /// Cinemeta video detail page — reads the aggregate Trakt state (watchlist / watched /
        /// rating) and projects it onto the entry shape instead of the anime-tracker read.
        /// Null / empty for anime ids.</param>
        [HttpGet("entries/{id}")]
        [RequireConfig]
        [ProducesResponseType(typeof(EntryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Entry(string id, int? season = null, string type = null)
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                // type=movie|series → this is a Cinemeta video tracked on Trakt, not an
                // anime-tracker entry. Read the aggregate Trakt state and project it onto
                // the modal's status / progress / score shape. service is reported as Trakt
                // so the modal picks the Trakt status set + score range; seasons is empty
                // (no cross-service franchise picker for general video). Mirrors the MVC
                // GetTraktVideoEntryAsync.
                if (type == "movie" || type == "series")
                {
                    var (vUid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                    var (vStatus, vProgress, vTotal, vScore) =
                        await GetTraktVideoStateAsync(vUid, type, id);
                    var vEntry = string.IsNullOrEmpty(vStatus)
                        ? null
                        : new AnimeEntry
                        {
                            MediaId = id,
                            Status = vStatus,
                            Progress = vProgress,
                            TotalEpisodes = vTotal > 0 ? vTotal : null,
                            Score = vScore,
                        };
                    return new JsonResult(new EntryResponse(
                        vEntry,
                        Seasons: new List<EntrySeason>(),
                        Service: (int)AnimeService.Trakt,
                        SelectedEntryId: id));
                }

                var animeService = tokenData.anime_service;

                // Resolve the per-cour seasons + selected entry id when the click came from a
                // card with a cross-service id (imdb:/tmdb:); for native ids (anilist:/kitsu:/
                // mal:) BuildSeasonsAsync short-circuits with an empty list and the original id.
                // isSeries is passed true because the id-prefix check inside BuildSeasonsAsync is
                // the real gate — movies with imdb/tmdb ids resolve to a single mapping and
                // return empty seasons anyway. videos:null skips auto-episode selection, which
                // the modal doesn't need (the user picks a cour manually).
                var (seasons, selectedEntryId, _) =
                    await BuildSeasonsAsync(id, isSeries: true, animeService, videos: null, season, episode: null);

                // Fetch the entry against the resolved per-mapping id rather than the raw imdb
                // id — that's what makes status / progress / score reflect the picked cour.
                var entry = animeService switch
                {
                    AnimeService.Anilist => await _anilistService.GetAnimeEntryAsync(tokenData, selectedEntryId, null),
                    AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, selectedEntryId, null),
                    // Trakt: reading a single anime entry back as an AnimeEntry isn't wired up
                    // until the Trakt list integration lands (Phase 3), so report "not on list"
                    // rather than calling Kitsu with a Trakt token. The write path still works.
                    AnimeService.Trakt => null,
                    _ => await _kitsuService.GetAnimeEntryAsync(tokenData, selectedEntryId, null),
                };

                // totalEpisodes comes from the entry when present; falls back to the matched
                // season's count when the user has no entry yet (so the progress input still
                // shows the right max). Stamp it onto the returned entry's TotalEpisodes.
                var totalEpisodes = entry?.TotalEpisodes
                    ?? seasons.FirstOrDefault(s => s.Id == selectedEntryId)?.TotalEpisodes;
                if (entry != null && !entry.TotalEpisodes.HasValue)
                    entry.TotalEpisodes = totalEpisodes;

                // Unlike the rest of the API, this endpoint returns 200 even when the entry is
                // null: a multi-cour franchise still needs its Seasons list so the modal can
                // render the dropdown and land on the "None" status for the unpicked cour.
                // (When there's no franchise either, the modal just shows an empty form.)
                return new JsonResult(new EntryResponse(
                    entry,
                    Seasons: seasons,
                    Service: (int)animeService,
                    SelectedEntryId: selectedEntryId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Entry failed (id={Id}, season={Season}).", id, season);
                return StatusCode(500, new ApiError("entry lookup failed"));
            }
        }

        /// <summary>
        /// Saves an entry on the primary provider and fans the change out to every
        /// linked secondary. Status / progress / score / notes / dates / rewatch count
        /// are accepted in the request body. An empty <c>status</c> is interpreted as
        /// "remove from list" — same convention as the Manage Entry page.
        ///
        /// The primary write surfaces synchronously: a 200 means it succeeded. The
        /// secondary fan-out is best-effort and runs concurrently in the background;
        /// per-target failures are swallowed and surface on the next <c>GET /linked</c>
        /// as <c>NeedsReauth</c>.
        /// </summary>
        /// <param name="id">Service-prefixed media id (<c>anilist:N</c>, <c>kitsu:N</c>,
        /// <c>mal:N</c>). Path segment; the body's id is intentionally absent so
        /// there's no ambiguity about which one wins.</param>
        /// <param name="season">Optional cour / season number for multi-cour franchises.
        /// Query param; same reasoning as <paramref name="id"/>.</param>
        /// <param name="request">Body — status / progress / score / notes / dates.
        /// Status is the canonical <see cref="ListStatus"/> enum and is translated
        /// to the primary provider's vocabulary server-side.</param>
        /// <param name="type">"movie" / "series" when saving a Cinemeta video entry —
        /// routes the write to Trakt (watchlist / history / rating) instead of the
        /// anime-tracker dispatch. Null / empty for anime ids.</param>
        [HttpPost("entries/{id}")]
        [RequireConfig]
        [ProducesResponseType(typeof(SaveEntryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SaveEntry(string id, [FromQuery] int? season,
            [FromBody] ApiSaveEntryRequest request, [FromQuery] string type = null)
        {
            try
            {
                if (request == null) return BadRequest(new ApiError("missing request body"));

                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                // type=movie|series → Cinemeta video tracked on Trakt; route to the Trakt
                // save (watchlist / history / ratings) instead of the anime-tracker
                // dispatch below. Mirrors the MVC SaveTraktVideoEntryAsync.
                if (type == "movie" || type == "series")
                {
                    var (vUid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                    if (string.IsNullOrEmpty(vUid))
                        return new JsonResult(new SaveEntryResponse(false, AnimeService.Trakt.ToString(), null));
                    // null status = "leave untouched" on the anime path, but the modal's
                    // Trakt save sends the explicit status (empty = remove), so map null → "".
                    var traktStatus = MapToTraktStatus(request.Status?.ToString());
                    var ok = await ApplyTraktVideoSaveAsync(vUid, type, id, traktStatus,
                        request.Progress, request.Score);
                    return new JsonResult(new SaveEntryResponse(ok, AnimeService.Trakt.ToString(),
                        Removed: string.IsNullOrEmpty(traktStatus) ? true : (bool?)null));
                }

                var startedAt = ParseDate(request.StartedAt);
                var finishedAt = ParseDate(request.FinishedAt);

                if (!request.Status.HasValue)
                {
                    // null status == delete from list. The DELETE method is the
                    // RESTful way to do this, but POST-with-null is the legacy path
                    // shared with the Manage Entry UI semantics.
                    switch (tokenData.anime_service)
                    {
                        case AnimeService.Anilist:
                            await _anilistService.DeleteAnimeEntryAsync(tokenData, id, season);
                            break;
                        case AnimeService.MyAnimeList:
                            await _malService.DeleteAnimeEntryAsync(tokenData, id, season);
                            break;
                        case AnimeService.Trakt:
                            await _traktService.DeleteEntryAsync(tokenData, id, season);
                            break;
                        default:
                            await _kitsuService.DeleteAnimeEntryAsync(tokenData, id, season);
                            break;
                    }
                    await _syncService.FanOutDeleteAsync(tokenData, id, season);
                    _listCache.Invalidate(tokenData);
                    return new JsonResult(new SaveEntryResponse(true, tokenData.anime_service.ToString(), Removed: true));
                }

                // Canonical → primary's vocabulary.
                var translatedStatus = TranslateStatusForService(request.Status.Value.ToString(), tokenData);

                switch (tokenData.anime_service)
                {
                    case AnimeService.Anilist:
                        await _anilistService.SaveAnimeEntryAsync(tokenData, id, season, request.Progress,
                            translatedStatus, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                    case AnimeService.MyAnimeList:
                        await _malService.SaveAnimeEntryAsync(tokenData, id, season, request.Progress,
                            translatedStatus, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                    case AnimeService.Trakt:
                        // Trakt takes the canonical status, not the per-service string:
                        // Planning → watchlist, otherwise progress → history.
                        await _traktService.SaveEntryAsync(tokenData, id, season, request.Progress,
                            planning: request.Status == ListStatus.Planning);
                        break;
                    default:
                        await _kitsuService.SaveAnimeEntryAsync(tokenData, id, season, request.Progress,
                            translatedStatus, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                }

                await _syncService.FanOutSaveAsync(tokenData, id, season, request.Progress,
                    translatedStatus, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);

                _listCache.Invalidate(tokenData);
                return new JsonResult(new SaveEntryResponse(true, tokenData.anime_service.ToString(), Removed: null));
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "API SaveEntry primary 401 (id={Id}).", id);
                return Unauthorized(new ApiError("primary token rejected by upstream"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API SaveEntry failed (id={Id}).", id);
                return StatusCode(500, new ApiError("save failed"));
            }
        }

        /// <summary>
        /// Removes an entry from the primary provider's list and fans the delete out
        /// to every linked secondary. Idempotent — deleting an entry that's not on
        /// the list returns 200.
        /// </summary>
        [HttpDelete("entries/{id}")]
        [RequireConfig]
        [ProducesResponseType(typeof(SaveEntryResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteEntry(string id, int? season = null)
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                switch (tokenData.anime_service)
                {
                    case AnimeService.Anilist:
                        await _anilistService.DeleteAnimeEntryAsync(tokenData, id, season);
                        break;
                    case AnimeService.MyAnimeList:
                        await _malService.DeleteAnimeEntryAsync(tokenData, id, season);
                        break;
                    case AnimeService.Trakt:
                        await _traktService.DeleteEntryAsync(tokenData, id, season);
                        break;
                    default:
                        await _kitsuService.DeleteAnimeEntryAsync(tokenData, id, season);
                        break;
                }
                await _syncService.FanOutDeleteAsync(tokenData, id, season);
                _listCache.Invalidate(tokenData);
                return new JsonResult(new SaveEntryResponse(true, tokenData.anime_service.ToString(), null));
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "API DeleteEntry primary 401 (id={Id}).", id);
                return Unauthorized(new ApiError("primary token rejected by upstream"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API DeleteEntry failed (id={Id}).", id);
                return StatusCode(500, new ApiError("delete failed"));
            }
        }

        /// <summary>
        /// Bulk save: writes every entry in the request body to the primary and fans
        /// each one out to linked secondaries. Processed sequentially per-entry so
        /// upstream rate limits aren't tripped; per-target fan-out within an entry
        /// stays concurrent. An empty <c>status</c> on an entry deletes it (same
        /// convention as the single-entry endpoint).
        ///
        /// Returns one result row per input entry plus rolled-up counts. Failures
        /// don't abort the run — useful for migrations where you'd rather see
        /// partial success than start over on a single bad row.
        /// </summary>
        [HttpPost("entries")]
        [RequireConfig]
        [ProducesResponseType(typeof(BulkSaveResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SaveEntriesBulk([FromBody] List<ApiBulkSaveEntry> entries)
        {
            try
            {
                if (entries == null || entries.Count == 0)
                    return BadRequest(new ApiError("request body must be a non-empty array of entries"));

                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                var results = new List<BulkSaveResult>();
                int ok = 0, failed = 0;

                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry?.Id))
                    {
                        failed++;
                        results.Add(new BulkSaveResult(null, false, null, "entry missing id"));
                        continue;
                    }

                    try
                    {
                        var startedAt = ParseDate(entry.StartedAt);
                        var finishedAt = ParseDate(entry.FinishedAt);

                        if (!entry.Status.HasValue)
                        {
                            switch (tokenData.anime_service)
                            {
                                case AnimeService.Anilist:
                                    await _anilistService.DeleteAnimeEntryAsync(tokenData, entry.Id, entry.Season);
                                    break;
                                case AnimeService.MyAnimeList:
                                    await _malService.DeleteAnimeEntryAsync(tokenData, entry.Id, entry.Season);
                                    break;
                                case AnimeService.Trakt:
                                    await _traktService.DeleteEntryAsync(tokenData, entry.Id, entry.Season);
                                    break;
                                default:
                                    await _kitsuService.DeleteAnimeEntryAsync(tokenData, entry.Id, entry.Season);
                                    break;
                            }
                            await _syncService.FanOutDeleteAsync(tokenData, entry.Id, entry.Season);
                            ok++;
                            results.Add(new BulkSaveResult(entry.Id, true, true, null));
                            continue;
                        }

                        var translatedStatus = TranslateStatusForService(entry.Status.Value.ToString(), tokenData);

                        switch (tokenData.anime_service)
                        {
                            case AnimeService.Anilist:
                                await _anilistService.SaveAnimeEntryAsync(tokenData, entry.Id, entry.Season, entry.Progress,
                                    translatedStatus, entry.Score, entry.Notes, entry.RewatchCount, startedAt, finishedAt);
                                break;
                            case AnimeService.MyAnimeList:
                                await _malService.SaveAnimeEntryAsync(tokenData, entry.Id, entry.Season, entry.Progress,
                                    translatedStatus, entry.Score, entry.Notes, entry.RewatchCount, startedAt, finishedAt);
                                break;
                            case AnimeService.Trakt:
                                await _traktService.SaveEntryAsync(tokenData, entry.Id, entry.Season, entry.Progress,
                                    planning: entry.Status == ListStatus.Planning);
                                break;
                            default:
                                await _kitsuService.SaveAnimeEntryAsync(tokenData, entry.Id, entry.Season, entry.Progress,
                                    translatedStatus, entry.Score, entry.Notes, entry.RewatchCount, startedAt, finishedAt);
                                break;
                        }
                        await _syncService.FanOutSaveAsync(tokenData, entry.Id, entry.Season, entry.Progress,
                            translatedStatus, entry.Score, entry.Notes, entry.RewatchCount, startedAt, finishedAt);
                        ok++;
                        results.Add(new BulkSaveResult(entry.Id, true, null, null));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        results.Add(new BulkSaveResult(entry.Id, false, null, ex.Message));
                    }
                }

                // One invalidation for the whole bulk run — every entry potentially
                // touches a different list type, so the per-entry blast radius
                // already covers all six list types via the all-types nuke.
                if (ok > 0) _listCache.Invalidate(tokenData);

                return new JsonResult(new BulkSaveResponse(
                    Primary: tokenData.anime_service.ToString(),
                    Ok: ok, Failed: failed, Total: entries.Count, Results: results));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API SaveEntriesBulk failed.");
                return StatusCode(500, new ApiError("bulk save failed"));
            }
        }

        /// <summary>
        /// Compares the primary's library against each linked secondary's library and
        /// surfaces what's out of sync. For each linked provider returns:
        ///
        ///   - <c>missing</c>: primary entries the secondary doesn't have at all.
        ///   - <c>mismatched</c>: entries present on both sides but with a different
        ///     normalised status or progress.
        ///
        /// Status comparison normalises across provider vocabularies so AniList
        /// <c>CURRENT</c>, Kitsu <c>current</c>, and MAL <c>watching</c> all collapse
        /// to a single canonical value. Score is intentionally not compared — it sits
        /// in different scales per provider and produces too many false positives.
        ///
        /// Useful for "your trackers are N entries out of sync" UIs and for deciding
        /// whether to run a full Sync from primary.
        /// </summary>
        [HttpGet("sync/diff")]
        [RequireConfig]
        [ProducesResponseType(typeof(DiffResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Diff()
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                var configuration = await ResolveConfigAsync(resolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid))
                    return BadRequest(new ApiError("config is not stored — diff requires a v5 install URL"));

                var linked = await _configStore.GetLinkedTokensAsync(uid);
                if (linked.Count == 0)
                    return new JsonResult(new
                    {
                        primary = tokenData.anime_service.ToString(),
                        diffs = Array.Empty<object>(),
                    });

                var primaryEntries = await _syncService.GetPrimaryEntriesAsync(tokenData);
                var diffs = new List<object>();

                foreach (var l in linked)
                {
                    if (l.NeedsReauth || l.TokenData == null)
                    {
                        diffs.Add(new { service = l.Service.ToString(), needsReauth = true });
                        continue;
                    }

                    List<AnimeEntry> secondaryEntries;
                    try
                    {
                        secondaryEntries = l.Service switch
                        {
                            AnimeService.Anilist => await _anilistService.GetUserListEntriesAsync(l.TokenData),
                            AnimeService.Kitsu => await _kitsuService.GetUserListEntriesAsync(l.TokenData),
                            AnimeService.MyAnimeList => await _malService.GetUserListEntriesAsync(l.TokenData),
                            _ => [],
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "API Diff secondary fetch failed (service={Service}).", l.Service);
                        diffs.Add(new { service = l.Service.ToString(), error = "secondary fetch failed" });
                        continue;
                    }

                    // Index secondary entries by their service-prefixed media id so we can
                    // resolve a primary entry's mapping in O(1) rather than walking the list.
                    var byId = secondaryEntries
                        .Where(e => !string.IsNullOrEmpty(e.MediaId))
                        .ToDictionary(e => e.MediaId, e => e);

                    var prefix = GetServicePrefix(l.Service);

                    var missing = new List<object>();
                    var mismatched = new List<object>();

                    foreach (var p in primaryEntries)
                    {
                        if (string.IsNullOrEmpty(p.MediaId)) continue;

                        var resolved = await _mappingService.GetIdByService(p.MediaId, l.Service);
                        if (string.IsNullOrEmpty(resolved)) continue;

                        var key = $"{prefix}{resolved}";
                        if (!byId.TryGetValue(key, out var s))
                        {
                            missing.Add(new { primary = new { p.MediaId, p.Status, p.Progress } });
                            continue;
                        }

                        var pStatus = NormalizeListStatus(p.Status);
                        var sStatus = NormalizeListStatus(s.Status);
                        if (pStatus != sStatus || p.Progress != s.Progress)
                        {
                            mismatched.Add(new
                            {
                                primary = new { p.MediaId, p.Status, p.Progress },
                                secondary = new { s.MediaId, s.Status, s.Progress },
                            });
                        }
                    }

                    diffs.Add(new
                    {
                        service = l.Service.ToString(),
                        missing,
                        mismatched,
                        missingCount = missing.Count,
                        mismatchedCount = mismatched.Count,
                    });
                }

                return new JsonResult(new
                {
                    primary = tokenData.anime_service.ToString(),
                    primaryCount = primaryEntries.Count,
                    diffs,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Diff failed.");
                return StatusCode(500, new ApiError("diff failed"));
            }
        }

        /// <summary>
        /// Promotes a linked secondary to primary. The previous primary moves into
        /// the linked-tokens array; the UID is preserved so existing install URLs
        /// keep working. Pass <c>force=true</c> to delete a colliding row when the
        /// target service already has a separate primary on a different UID — this
        /// nukes the other row's flags and install URL, so callers should confirm
        /// with the user before sending it.
        /// </summary>
        [HttpPost("primary/{service}")]
        [RequireConfig]
        [ProducesResponseType(typeof(PromoteResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(PromoteResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(PromoteResponse), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Promote(AnimeService service, bool force = false)
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var configuration = await ResolveConfigAsync(resolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid))
                    return BadRequest(new ApiError("config is not stored — only v5 install URLs can swap primary"));

                var (newPrimary, reason) = await _configStore.SwapPrimaryAsync(uid, service, force);
                if (newPrimary == null)
                {
                    var status = reason switch
                    {
                        "collision" => StatusCodes.Status409Conflict,
                        "needs-reauth" => StatusCodes.Status401Unauthorized,
                        "not-linked" or "no-primary" or "uid-missing" => StatusCodes.Status404NotFound,
                        _ => StatusCodes.Status400BadRequest,
                    };
                    return StatusCode(status, new PromoteResponse(false, null, reason));
                }

                return new JsonResult(new PromoteResponse(true, newPrimary.anime_service.ToString(), null));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Promote failed (service={Service}).", service);
                return StatusCode(500, new ApiError("promote failed"));
            }
        }

        /// <summary>
        /// Walks the primary's full library and fans every entry out to every linked
        /// secondary. Streams <c>application/x-ndjson</c> progress lines so a long-
        /// running run gives the caller real-time feedback. Closing the connection
        /// cancels mid-run via <see cref="HttpContext.RequestAborted"/>.
        ///
        /// Output stream: one JSON object per line. Stages:
        /// <code>
        /// {"stage":"start","total":127}
        /// {"stage":"progress","id":"anilist:21","ok":true,"processed":1,"total":127}
        /// {"stage":"progress","id":"anilist:1","ok":false,"error":"…","processed":2,"failed":1,"total":127}
        /// {"stage":"done","processed":127,"failed":3,"total":127}
        /// </code>
        ///
        /// Per-target fan-out failures (Crunchyroll-style "this Kitsu link is dead")
        /// stay swallowed inside SyncService — surface on the next <c>GET /linked</c>
        /// as <c>NeedsReauth</c>. Per-entry errors here mean "the SyncService call
        /// itself threw" (network / 5xx / rate limit).
        /// </summary>
        // ApiExplorer can't infer a schema for the streaming Task return — mark this
        // one ignored so swagger.json generates cleanly. Hand-document the streaming
        // contract via the docstring above.
        [ApiExplorerSettings(IgnoreApi = true)]
        [HttpPost("sync")]
        public async Task Sync()
        {
            var ct = HttpContext.RequestAborted;

            Response.ContentType = "application/x-ndjson";
            Response.Headers["Cache-Control"] = "no-cache";

            try
            {
                var configHeader = Request.Headers[RequireConfigAttribute.HeaderName].FirstOrDefault();
                var resolvedConfig = string.IsNullOrWhiteSpace(configHeader) ? null : configHeader.Trim();
                if (resolvedConfig == null)
                {
                    Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await WriteJsonLineAsync(new { stage = "error", error = $"{RequireConfigAttribute.HeaderName} header required" }, ct);
                    return;
                }
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                {
                    Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await WriteJsonLineAsync(new { stage = "error", error = "config has no primary token" }, ct);
                    return;
                }

                var entries = await _syncService.GetPrimaryEntriesAsync(tokenData);
                await WriteJsonLineAsync(new { stage = "start", total = entries.Count }, ct);

                int processed = 0, failed = 0;
                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        await _syncService.FanOutSaveAsync(tokenData, entry.MediaId, null,
                            entry.Progress, entry.Status, entry.Score, entry.Notes,
                            entry.RewatchCount, entry.StartedAt, entry.FinishedAt);
                        processed++;
                        await WriteJsonLineAsync(new
                        {
                            stage = "progress",
                            id = entry.MediaId,
                            ok = true,
                            processed,
                            total = entries.Count,
                        }, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failed++;
                        await WriteJsonLineAsync(new
                        {
                            stage = "progress",
                            id = entry.MediaId,
                            ok = false,
                            error = ex.Message,
                            processed,
                            failed,
                            total = entries.Count,
                        }, ct);
                    }
                }

                await WriteJsonLineAsync(new
                {
                    stage = "done",
                    processed,
                    failed,
                    total = entries.Count,
                    cancelled = ct.IsCancellationRequested,
                }, ct);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected mid-run — nothing to send back.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Sync failed.");
                try { await WriteJsonLineAsync(new { stage = "error", error = "sync failed" }, ct); }
                catch { /* response may already be partially written */ }
            }
        }

        private async Task WriteJsonLineAsync(object payload, CancellationToken ct)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            await Response.WriteAsync(json + "\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        private static DateTime? ParseDate(string s) =>
            DateTime.TryParse(s, out var dt) ? dt : null;

        // The Config UID, populated by [RequireConfig] from the X-AniSync-Config header.
        // Actions decorated with [RequireConfig] can read this directly; the streaming
        // Sync endpoint that needs to emit a JSON-line error frame instead of a 401
        // body inspects the raw header itself.
        private string ResolvedConfig =>
            (string)HttpContext.Items[RequireConfigAttribute.ItemKey]!;

        // ── Insight endpoints (user-scoped, read-only) ──────────────────────────

        /// <summary>
        /// User's AniList statistics — counts per status, mean score, total
        /// hours watched. Sourced from AniList's <c>User.statistics</c> GraphQL
        /// (one query, vastly cheaper than walking the Watching + Completed
        /// lists locally). Returns 404 when the user has neither an AniList
        /// primary nor a linked AniList account.
        /// </summary>
        [HttpGet("stats")]
        [RequireConfig]
        [ProducesResponseType(typeof(UserStatsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Stats()
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                // Primary is AniList: use it directly. Otherwise look for an
                // AniList token in the linked-secondaries list — same fallback
                // the dashboard uses (HomeController.cs:127-143).
                TokenData anilistToken = null;
                if (tokenData.anime_service == AnimeService.Anilist && !tokenData.anonymousUser)
                {
                    anilistToken = tokenData;
                }
                else
                {
                    var cfg = await ResolveConfigAsync(resolvedConfig, _configStore);
                    if (!string.IsNullOrEmpty(cfg?.tokenUid))
                    {
                        var linked = await _configStore.GetLinkedTokensAsync(cfg.tokenUid);
                        anilistToken = linked
                            .FirstOrDefault(l => !l.NeedsReauth
                                && l.TokenData != null
                                && !l.TokenData.anonymousUser
                                && l.Service == AnimeService.Anilist)?.TokenData;
                    }
                }

                if (anilistToken == null)
                    return NotFound(new ApiError("no AniList token (primary or linked) available"));

                var stats = await _anilistService.GetUserStatsAsync(anilistToken);
                if (stats == null) return NotFound(new ApiError("AniList returned no stats"));
                return new JsonResult(new UserStatsResponse(stats));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Stats failed.");
                return StatusCode(500, new ApiError("stats lookup failed"));
            }
        }

        /// <summary>
        /// Aggregate Trakt watch stats (movies / shows / episodes / hours) for the
        /// dashboard "Your stats" video row — the API twin of the web's
        /// <c>Home/TraktStatsData</c> (which the thin clients can't reach: the UI
        /// HomeController is filtered out of the Blazor host). Sourced from Trakt's
        /// users stats endpoint. Returns 404 when Trakt isn't connected (or the
        /// upstream blips), which the client treats as "hide the video stats row".
        /// </summary>
        [HttpGet("trakt-stats")]
        [RequireConfig]
        [ProducesResponseType(typeof(TraktUserStats), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> TraktStats()
        {
            try
            {
                var configuration = await ResolveConfigAsync(ResolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid))
                    return NotFound(new ApiError("config is not stored"));

                var stats = await _traktService.GetUserStatsAsync(uid);
                if (stats == null) return NotFound(new ApiError("Trakt is not connected"));
                return new JsonResult(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Trakt stats failed.");
                return StatusCode(500, new ApiError("trakt stats lookup failed"));
            }
        }

        /// <summary>
        /// Continue-watching shelf — the user's <c>Current</c>/Watching list
        /// from their primary tracker, capped at <paramref name="limit"/>
        /// items. Same data the dashboard's "Continue your anime journey"
        /// shelf renders, in the standard <c>Meta</c> shape (poster +
        /// per-episode progress + airingAt for the next-episode pill).
        /// </summary>
        /// <param name="limit">Max items to return. 1–50. Defaults to 15.</param>
        [HttpGet("continue-watching")]
        [RequireConfig]
        [ProducesResponseType(typeof(ContinueWatchingResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ContinueWatching(int limit = 15)
        {
            if (limit < 1) limit = 1;
            if (limit > 50) limit = 50;
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                // The shelf is the same Watching set as the /library Current grid, just capped for a
                // scroll row — so it shares the exact merged fan-out (primary + linked secondaries, with
                // the user's season-grouping / hide-unaired / 18+ prefs applied) via GetMergedUserListAsync.
                // ResolvedConfig is the raw header string; ResolveConfigAsync decodes it (v3 inline or v5
                // store-backed) into the Configuration that carries the flag bits.
                var configuration = await ResolveConfigAsync(resolvedConfig, _configStore);
                var metas = await GetMergedUserListAsync(tokenData, configuration, ListType.Current);

                // Sort alphabetically (same comparer the /library Current grid uses) BEFORE the cap, so the
                // shelf shows the same first-N titles the top of the library does — not an arbitrary 15 in
                // the tracker's native list order.
                var items = metas
                    .OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Take(limit)
                    .ToList();

                return new JsonResult(new ContinueWatchingResponse(
                    Primary: tokenData.anime_service.ToString(),
                    Items: items));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API ContinueWatching failed.");
                return StatusCode(500, new ApiError("continue-watching lookup failed"));
            }
        }

        /// <summary>
        /// The one user-list fetch behind both the dashboard Continue watching shelf
        /// (<see cref="ContinueWatching"/>) and the /library grid (<see cref="List"/>): fan out to the
        /// primary + every healthy linked secondary (deduped) via <see cref="IMergedListService"/> — the
        /// same merge the MVC <c>LibraryController</c>/dashboard use — so every list surface shows the same
        /// entries. Season-grouping / hide-unaired / 18+ all come from the resolved <paramref
        /// name="configuration"/> so the surfaces can't filter differently. Falls back to the primary
        /// service alone only when the uid can't be resolved (v3 inline config, no linked secondaries to
        /// merge anyway). The callers post-process: the shelf caps the result, the grid sorts/searches it.
        /// </summary>
        private async Task<List<Meta>> GetMergedUserListAsync(
            TokenData tokenData, Configuration configuration, ListType listType, string genre = null)
        {
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var hideUnreleased = configuration?.hideUnreleasedFromWatching == true;
            var hideAdult = configuration?.showAdultContent != true;

            var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
            if (!string.IsNullOrEmpty(uid))
            {
                return await _mergedListService.GetMergedListAsync(
                    tokenData, uid, listType, genre, groupSeasons, hideUnreleased, hideAdult) ?? [];
            }

            return (tokenData.anime_service switch
            {
                AnimeService.Anilist => await _anilistService.GetAnimeListAsync(tokenData, listType, genre: genre, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listType, genre: genre, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                _ => await _kitsuService.GetAnimeListAsync(tokenData, listType, genre: genre, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
            }) ?? [];
        }

        /// <summary>
        /// The user's list for a given status, in the <c>Meta</c> shape (poster +
        /// progress) so a client can render a poster grid — the data behind the
        /// /library tabs. <paramref name="status"/> maps to a tracker list type:
        /// watching/current, completed, planning, paused, dropped, rewatching.
        /// Defaults to the Watching list.
        /// </summary>
        [HttpGet("list")]
        [RequireConfig]
        [ProducesResponseType(typeof(MetaListResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> List(string status = null, string genre = null, string search = null)
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                var listType = (status ?? "").Trim().ToLowerInvariant() switch
                {
                    "completed" => ListType.Completed,
                    "planning" => ListType.Planning,
                    "paused" => ListType.Paused,
                    "dropped" => ListType.Dropped,
                    "rewatching" or "repeating" => ListType.Repeating,
                    _ => ListType.Current,
                };

                // Same merged fan-out the dashboard Continue watching shelf uses (see
                // GetMergedUserListAsync) — primary + linked secondaries, deduped, with the user's
                // season-grouping / hide-unaired / 18+ prefs applied — so the library grid and the shelf
                // never drift. genre is an in-list discover filter threaded straight through.
                var configuration = await ResolveConfigAsync(resolvedConfig, _configStore);
                var metas = await GetMergedUserListAsync(tokenData, configuration, listType, genre);

                // Search → relevance-ranked (ScoreMatch >= 0.4); otherwise alphabetical. Mirrors
                // LibraryController.Page so the API and the MVC view agree.
                if (!string.IsNullOrWhiteSpace(search) && metas.Count > 0)
                {
                    var q = Utils.NormalizeTitle(search);
                    metas = metas
                        .Select(m => (meta: m, score: Utils.ScoreMatch(q, m.name)))
                        .Where(x => x.score >= 0.4)
                        .OrderByDescending(x => x.score)
                        .Select(x => x.meta)
                        .ToList();
                }
                else
                {
                    metas = metas.OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
                }
                return new JsonResult(new MetaListResponse(metas));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API List failed (status={Status}).", status);
                return StatusCode(500, new ApiError("list lookup failed"));
            }
        }

        /// <summary>
        /// Episodes airing in the next 24h that match this user's "Watching"
        /// list — same source the bell uses to fire notifications. Reads from
        /// the in-memory <see cref="IAnimeScheduleService"/> snapshot
        /// (refreshed daily at UTC midnight) cross-referenced against the
        /// persistent <c>user_watching_cache</c>. The user's Watching cache
        /// must have been populated at least once (happens at login or via
        /// the daily backstop refresh); empty list otherwise.
        /// </summary>
        [HttpGet("upcoming")]
        [RequireConfig]
        [ProducesResponseType(typeof(UserUpcomingResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Upcoming()
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var cfg = await ResolveConfigAsync(resolvedConfig, _configStore);
                if (string.IsNullOrEmpty(cfg?.tokenUid))
                    return new JsonResult(new UserUpcomingResponse([]));

                var cache = await _watchingCache.GetAsync(cfg.tokenUid);
                if (cache == null || cache.MediaIds.Count == 0)
                    return new JsonResult(new UserUpcomingResponse([]));

                var schedule = _scheduleService.GetSchedule();
                if (schedule.Count == 0)
                    return new JsonResult(new UserUpcomingResponse([]));

                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var matches = new List<UserUpcomingEpisode>();
                foreach (var sched in schedule)
                {
                    if (sched.AiringAt <= nowUnix) continue;

                    // Resolve the AniList id into this user's primary service
                    // (so the response matches what their list contains). Mapping
                    // miss → MAL/Kitsu users for this show silently skip, same
                    // pattern as the episode-notification dispatcher.
                    var idInUserSpace = cache.Service switch
                    {
                        AnimeService.Anilist => $"{anilistPrefix}{sched.AnilistId}",
                        _ => await _mappingService.GetIdWithPrefixAsync(
                                $"{anilistPrefix}{sched.AnilistId}", cache.Service),
                    };
                    if (string.IsNullOrEmpty(idInUserSpace)) continue;
                    if (!cache.MediaIds.Contains(idInUserSpace)) continue;

                    matches.Add(new UserUpcomingEpisode(
                        AnimeId: idInUserSpace,
                        Title: sched.Title,
                        Episode: sched.Episode,
                        AiringAt: sched.AiringAt,
                        CoverImage: sched.CoverImage));
                }

                // Same chronological order the dispatcher would fire in.
                matches.Sort((a, b) => a.AiringAt.CompareTo(b.AiringAt));
                return new JsonResult(new UserUpcomingResponse(matches));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Upcoming failed.");
                return StatusCode(500, new ApiError("upcoming lookup failed"));
            }
        }

        /// <summary>
        /// Lists every linked secondary provider for this config plus the
        /// <c>NeedsReauth</c> flag, so a client can show the same status pills the
        /// configure page does without needing the v5 install URL.
        /// </summary>
        [HttpGet("linked")]
        [RequireConfig]
        [ProducesResponseType(typeof(LinkedResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Linked()
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                // Only v5 (UID-only) URLs have linked tokens — v3 inline anonymous URLs can
                // only carry the primary, so the linked list is necessarily empty.
                // ResolveConfigAsync decodes the install URL and pulls tokenUid out of
                // either the inline JSON (v4) or the embedded UID byte segment (v5).
                var configuration = await ResolveConfigAsync(resolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                var linked = string.IsNullOrEmpty(uid)
                    ? new List<LinkedToken>()
                    : await _configStore.GetLinkedTokensAsync(uid);

                return new JsonResult(new LinkedResponse(
                    Primary: tokenData.anime_service.ToString(),
                    Linked: linked
                        .Select(l => new LinkedSummary(l.Service.ToString(), l.NeedsReauth))
                        .ToList()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Linked failed.");
                return StatusCode(500, new ApiError("linked lookup failed"));
            }
        }

        /// <summary>
        /// The user's Hidden section — titles tucked away from Discover, most-recently-
        /// hidden first, in the Meta shape for a poster grid. Paginated by skip (24/page).
        /// Empty for inline (v3) configs with no stored uid (nothing to hide against).
        /// </summary>
        [HttpGet("hidden")]
        [RequireConfig]
        [ProducesResponseType(typeof(MetaListResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Hidden(string skip = null, string type = null)
        {
            try
            {
                var configuration = await ResolveConfigAsync(ResolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid)) return new JsonResult(new MetaListResponse([]));

                const int pageSize = 24;
                var offset = int.TryParse(skip, out var s) && s > 0 ? s : 0;
                // Filter to the active media mode when the client passes ?type= (anime/movie/series),
                // so the Hidden view shows only that mode; absent → all hidden entries (back-compat).
                var mediaType = type is "anime" or "movie" or "series" ? type : null;
                var hidden = await _hiddenStore.GetPageAsync(uid, pageSize, offset, mediaType);
                return new JsonResult(new MetaListResponse(hidden.Select(ToHiddenMeta).ToList()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Hidden failed.");
                return StatusCode(500, new ApiError("hidden lookup failed"));
            }
        }

        // Projects a stored hidden entry into the Meta shape the poster grid renders —
        // id / name / poster / type only (the section is a flat restore-list). Mirrors
        // DiscoverController.ToHiddenMeta so the web + thin-client Hidden views match.
        private static Meta ToHiddenMeta(HiddenEntry h) => new()
        {
            id = h.Id,
            name = h.Title,
            poster = h.ImageUrl,
            type = string.IsNullOrEmpty(h.MediaType) ? MetaType.anime.ToString() : h.MediaType,
        };

        // ── Detail-page per-user state + interactive toggles ────────────────────
        // The header-authed twin of the MVC Detail page's server-computed
        // Model.Entry / Model.IsHidden + the heart / hide toggles
        // (MetaController.ToggleWatchingByApi / ToggleHiddenByApi). The Blazor
        // Detail page calls these to drive the user-state pill, the quick-add
        // heart, and the Hide / Unhide button — none of which the token-less
        // /api/v1/anime/{id} can populate. Anime path only; the movie / series
        // (Trakt) tracking branch is deferred (see VideoDetail follow-up).

        /// <summary>
        /// The logged-in user's state for one anime detail page, in a single call:
        /// the primary provider's list entry (status / progress / total) plus the
        /// Hide-from-Discover flag. Mirrors how the MVC Detail action computes
        /// <c>Model.Entry</c> + <c>Model.IsHidden</c> server-side at once so the
        /// hero's pill, heart, and hide button all render their initial state from
        /// one round-trip. Status comes back as the raw provider value (the client
        /// normalises it the same way the page's PrettyStatus does); fields are null
        /// / false when the anime isn't on the user's list or isn't hidden.
        /// </summary>
        /// <param name="id">Service-prefixed anime id, or a Cinemeta video id (<c>tt…</c>) when <paramref name="type"/> is set.</param>
        /// <param name="season">Optional cour / season number for multi-cour franchises.</param>
        /// <param name="type">"movie" / "series" when the detail page is a Cinemeta
        /// video (tracked on Trakt) rather than an anime — routes the entry read to
        /// the user's Trakt state. Null / empty for anime ids.</param>
        [HttpGet("state/{id}")]
        [RequireConfig]
        [ProducesResponseType(typeof(DetailStateResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> DetailState(string id, int? season = null, string type = null)
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);

                // Hidden-from-Discover state for the Hide / Unhide button. Only
                // meaningful for stored (v5) configs with a uid; best-effort.
                var isHidden = false;
                if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(id))
                {
                    try { isHidden = await _hiddenStore.IsHiddenAsync(uid, id); }
                    catch (Exception ex) { _logger.LogWarning(ex, "API DetailState hidden lookup failed (id={Id}).", id); }
                }

                // Movie / series → the tracker is Trakt, not the anime providers.
                // Project the aggregate Trakt state (watchlist / watched / rating)
                // onto the pill's status / progress / score shape, mirroring the MVC
                // VideoDetail hero pill. TraktConnected gates the pill + heart the same
                // way (traktToken.Connected). Anime falls through to the provider read.
                if (type == "movie" || type == "series")
                {
                    var traktConnected = false;
                    if (!string.IsNullOrEmpty(uid))
                    {
                        try
                        {
                            var traktToken = await _configStore.GetTraktTokenAsync(uid);
                            traktConnected = traktToken?.Connected == true;
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "API DetailState trakt-token lookup failed (id={Id}).", id); }
                    }

                    // Only read the Trakt entry when actually connected — keeps the
                    // status pill empty ("Add to List") for an unconnected viewer.
                    var (vStatus, vProgress, vTotal, vScore) = traktConnected
                        ? await GetTraktVideoStateAsync(uid, type, id)
                        : ("", 0, type == "series" ? (int?)null : 1, (double?)null);
                    return new JsonResult(new DetailStateResponse(
                        OnList: !string.IsNullOrEmpty(vStatus),
                        Status: string.IsNullOrEmpty(vStatus) ? null : vStatus,
                        Progress: vProgress,
                        TotalEpisodes: vTotal > 0 ? vTotal : null,
                        Score: vScore,
                        IsHidden: isHidden,
                        TraktConnected: traktConnected));
                }

                // The user's list entry against the primary's id-space. Anonymous
                // configs have no list to read; everyone else resolves the id into
                // the primary service and fetches the entry (best-effort).
                AnimeEntry entry = null;
                if (!tokenData.anonymousUser)
                {
                    try
                    {
                        var entryId = await ResolvePrimaryEntryIdAsync(id, tokenData.anime_service, season);
                        if (!string.IsNullOrEmpty(entryId))
                        {
                            entry = tokenData.anime_service switch
                            {
                                AnimeService.Anilist => await _anilistService.GetAnimeEntryAsync(tokenData, entryId, null),
                                AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, entryId, null),
                                AnimeService.Trakt => null,
                                _ => await _kitsuService.GetAnimeEntryAsync(tokenData, entryId, null),
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "API DetailState entry fetch failed (id={Id}).", id);
                    }
                }

                var hasEntry = entry != null && !string.IsNullOrEmpty(entry.Status);
                return new JsonResult(new DetailStateResponse(
                    OnList: hasEntry,
                    Status: hasEntry ? entry.Status : null,
                    Progress: hasEntry ? entry.Progress : 0,
                    TotalEpisodes: hasEntry ? entry.TotalEpisodes : null,
                    Score: hasEntry ? entry.Score : null,
                    IsHidden: isHidden));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API DetailState failed (id={Id}).", id);
                return StatusCode(500, new ApiError("state lookup failed"));
            }
        }

        /// <summary>
        /// One-click "heart" toggle for the Currently Watching list, backing the
        /// outline / filled heart on the anime detail page. Ports
        /// <c>MetaController.ToggleWatchingByApi</c> (anime path): resolves the
        /// entry's current membership server-side, then —
        ///   - in a non-Watching list (Completed / Planning / Paused / Dropped /
        ///     Rewatching): no-op, returns <c>hidden:true</c> so the client drops
        ///     the heart (those are managed via the Manage Entry modal);
        ///   - already Watching: removes the entry (delete + fan-out), returns
        ///     <c>watching:false</c>;
        ///   - not on any list: adds it to Watching, preserving any progress the
        ///     entry already carried, returns <c>watching:true</c>.
        /// Reuses the same per-service save / delete + fan-out + cache-invalidation
        /// path the Manage Entry save runs, pinned to the Watching status.
        /// </summary>
        [HttpPost("watching/toggle")]
        [RequireConfig]
        [ProducesResponseType(typeof(ToggleWatchingResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ToggleWatching([FromBody] ToggleEntryRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request?.Id))
                    return BadRequest(new ApiError("missing id"));

                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token) || tokenData.anonymousUser)
                    return Unauthorized(new ApiError("config has no primary token"));

                // Movie / series → the tracker is Trakt, not the anime providers.
                // Route to the Trakt watching toggle, which mirrors this method's
                // membership logic (in another list → hide; watching → remove;
                // not tracked → add) on top of Trakt's vocabulary.
                if (request.Type == "movie" || request.Type == "series")
                {
                    var (vUid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                    return await ToggleTraktWatchingAsync(vUid, request);
                }

                var animeService = tokenData.anime_service;
                // Trakt primary's per-anime read-back isn't wired up yet (same gap the
                // Entry endpoint notes), so treat it as "not on list" → add to Watching.
                var entryId = await ResolvePrimaryEntryIdAsync(request.Id, animeService, request.Season);
                if (string.IsNullOrEmpty(entryId))
                    return BadRequest(new ApiError("anime not available on your primary"));

                var entry = animeService switch
                {
                    AnimeService.Anilist => await _anilistService.GetAnimeEntryAsync(tokenData, entryId, null),
                    AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, entryId, null),
                    AnimeService.Trakt => null,
                    _ => await _kitsuService.GetAnimeEntryAsync(tokenData, entryId, null),
                };

                var norm = NormalizeListStatus(entry?.Status);

                // Entry lives in some other list — the heart isn't the control for
                // that (the modal is). Tell the client to hide it; change nothing.
                if (norm != null && norm != "watching")
                    return new JsonResult(new ToggleWatchingResponse(Ok: false, Watching: false, Hidden: true));

                if (norm == "watching")
                {
                    // Currently Watching → remove from the list entirely.
                    switch (animeService)
                    {
                        case AnimeService.Anilist:
                            await _anilistService.DeleteAnimeEntryAsync(tokenData, entryId, null);
                            break;
                        case AnimeService.MyAnimeList:
                            await _malService.DeleteAnimeEntryAsync(tokenData, entryId, null);
                            break;
                        default:
                            await _kitsuService.DeleteAnimeEntryAsync(tokenData, entryId, null);
                            break;
                    }
                    await _syncService.FanOutDeleteAsync(tokenData, entryId, null);
                    _listCache.Invalidate(tokenData);
                    return new JsonResult(new ToggleWatchingResponse(Ok: true, Watching: false, Hidden: false));
                }

                // Not on any list → add to Watching, keeping any progress the entry
                // already had (0 for a brand-new entry). Status is translated to the
                // provider's native vocabulary just like the modal save does.
                var watchingStatus = TranslateStatusForService("watching", tokenData);
                var progress = entry?.Progress ?? 0;
                switch (animeService)
                {
                    case AnimeService.Anilist:
                        await _anilistService.SaveAnimeEntryAsync(tokenData, entryId, null, progress,
                            watchingStatus, null, null, null, null, null);
                        break;
                    case AnimeService.MyAnimeList:
                        await _malService.SaveAnimeEntryAsync(tokenData, entryId, null, progress,
                            watchingStatus, null, null, null, null, null);
                        break;
                    default:
                        await _kitsuService.SaveAnimeEntryAsync(tokenData, entryId, null, progress,
                            watchingStatus, null, null, null, null, null);
                        break;
                }
                await _syncService.FanOutSaveAsync(tokenData, entryId, null, progress,
                    watchingStatus, null, null, null, null, null);
                _listCache.Invalidate(tokenData);
                return new JsonResult(new ToggleWatchingResponse(Ok: true, Watching: true, Hidden: false));
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "API ToggleWatching primary 401 (id={Id}).", request?.Id);
                return Unauthorized(new ApiError("primary token rejected by upstream"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API ToggleWatching failed (id={Id}).", request?.Id);
                return StatusCode(500, new ApiError("toggle failed"));
            }
        }

        /// <summary>
        /// Hide / unhide an anime from the user's Discover catalogs, backing the
        /// Hide / Unhide button on the detail page. Ports
        /// <c>MetaController.ToggleHiddenByApi</c>: toggles a row in the per-user
        /// <c>hidden_entries</c> store; the display fields (title / poster / media
        /// type) are cached at write time so the Discover "Hidden" section renders
        /// the card without re-fetching the provider. Requires a stored (v5) config
        /// with a uid (inline configs have nothing to hide against).
        /// </summary>
        [HttpPost("hidden/toggle")]
        [RequireConfig]
        [ProducesResponseType(typeof(ToggleHiddenResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ToggleHidden([FromBody] ToggleHideRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request?.Id))
                    return BadRequest(new ApiError("missing id"));

                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token) || tokenData.anonymousUser)
                    return Unauthorized(new ApiError("config has no primary token"));

                var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                if (string.IsNullOrEmpty(uid))
                    return BadRequest(new ApiError("config is not stored — hide requires a v5 install URL"));

                if (await _hiddenStore.IsHiddenAsync(uid, request.Id))
                {
                    await _hiddenStore.RemoveAsync(uid, request.Id);
                    return new JsonResult(new ToggleHiddenResponse(Ok: true, Hidden: false));
                }

                await _hiddenStore.AddAsync(uid, new HiddenEntry
                {
                    Id = request.Id,
                    Title = request.Title,
                    ImageUrl = request.ImageUrl,
                    MediaType = string.IsNullOrEmpty(request.MediaType) ? "anime" : request.MediaType,
                });
                return new JsonResult(new ToggleHiddenResponse(Ok: true, Hidden: true));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API ToggleHidden failed (id={Id}).", request?.Id);
                return StatusCode(500, new ApiError("hide toggle failed"));
            }
        }

        // ── Trakt video (movie / series) tracking helpers ───────────────────────
        // Port of the MVC MetaController's Trakt video branch — the tracker behind
        // the video detail page's status pill, quick-add heart, and Manage Entry
        // modal. Shared by the DetailState / Entry / SaveEntry / ToggleWatching
        // type=movie|series branches above.

        /// <summary>
        /// Reads the aggregate Trakt state for one movie / series and projects it onto
        /// the (status, progress, totalEpisodes, score) shape the detail page renders.
        /// Series total comes from Cinemeta's episode list; a movie is one unit. Empty
        /// status + 0 progress when there's no uid or the title isn't tracked. Mirrors
        /// the MVC GetTraktVideoEntryAsync / VideoDetail pill derivation.
        /// </summary>
        private async Task<(string Status, int Progress, int? TotalEpisodes, double? Score)>
            GetTraktVideoStateAsync(string uid, string type, string id)
        {
            if (string.IsNullOrEmpty(uid)) return ("", 0, null, null);

            int? totalEpisodes = 1;
            if (type == "series")
            {
                var meta = await _cinemeta.GetVideoMetaAsync(type, id);
                totalEpisodes = meta?.videos?.Count;
            }

            var entry = await _traktService.GetVideoEntryAsync(uid, type, id);
            var (status, progress) = DeriveTraktVideoStatus(type, entry, totalEpisodes);
            return (status, progress, totalEpisodes, entry.Rating);
        }

        // Projects a TraktVideoEntry onto the pill/modal's (status, progress) shape.
        // Status vocabulary matches the modal's Trakt option set: planning / watching /
        // completed (or "" = not tracked), with the custom-status personal lists
        // (onhold / dropped / rewatching) taking precedence. Ported verbatim from
        // MetaController.Web.cs DeriveTraktVideoStatus.
        private static (string Status, int Progress) DeriveTraktVideoStatus(string type, TraktVideoEntry e, int? total)
        {
            // Custom-status personal lists (On Hold / Dropped / Rewatching) win
            // over the native surfaces — that's the explicit status the user set.
            if (!string.IsNullOrEmpty(e.CustomStatus))
                return (e.CustomStatus, e.WatchedEpisodes);

            if (type == "movie")
                return (e.Watched ? "completed"
                    : e.InPlayback ? "watching"   // left part-watched (paused playback)
                    : e.InWatchlist ? "planning"
                    : "", e.Watched ? 1 : 0);

            // An active episode playback means "watching" (continue-watching);
            // watched history without one means "completed". Mirrors movies and
            // avoids comparing Trakt's watched count to Cinemeta's episode total.
            var progress = e.WatchedEpisodes;
            var status = e.InPlayback ? "watching"
                : progress > 0 ? "completed"
                : e.InWatchlist ? "planning"
                : "";
            return (status, progress);
        }

        /// <summary>
        /// Trakt-backed half of the Currently Watching heart toggle, for movie / series
        /// detail pages. Mirrors <see cref="ToggleWatching"/>'s membership logic against
        /// Trakt's status vocabulary: an entry already in some other list (Planning /
        /// Completed / On Hold / Dropped) returns <c>hidden:true</c> so the client drops
        /// the heart; "watching" removes the entry; not-tracked adds it as Watching.
        /// Ported from MetaController.ToggleTraktWatchingAsync.
        /// </summary>
        private async Task<IActionResult> ToggleTraktWatchingAsync(string uid, ToggleEntryRequest request)
        {
            if (string.IsNullOrEmpty(uid))
                return new JsonResult(new ToggleWatchingResponse(Ok: false, Watching: false, Hidden: false));

            var entry = await _traktService.GetVideoEntryAsync(uid, request.Type, request.Id);
            var (status, _) = DeriveTraktVideoStatus(request.Type, entry, null);
            var norm = NormalizeListStatus(status);

            // In some other list — the heart isn't the control for that.
            if (norm != null && norm != "watching")
                return new JsonResult(new ToggleWatchingResponse(Ok: false, Watching: false, Hidden: true));

            // Watching → remove (status ""); not tracked → add as Watching.
            var newStatus = norm == "watching" ? string.Empty : "watching";
            var ok = await ApplyTraktVideoSaveAsync(uid, request.Type, request.Id, newStatus, progress: 0, score: null);
            if (!ok) return new JsonResult(new ToggleWatchingResponse(Ok: false, Watching: false, Hidden: false));
            return new JsonResult(new ToggleWatchingResponse(Ok: true, Watching: norm != "watching", Hidden: false));
        }

        /// <summary>
        /// Core Trakt movie/series write shared by the Manage Entry modal save and the
        /// quick-add heart. Maps the status onto watchlist / history / rating actions
        /// and returns whether the upstream write succeeded. For a series, "watching"
        /// marks episodes 1..progress and "completed" marks them all watched — the
        /// (season, episode) coords come from Cinemeta's ordered episode list. Ported
        /// from MetaController.ApplyTraktVideoSaveAsync.
        /// </summary>
        private async Task<bool> ApplyTraktVideoSaveAsync(string uid, string type, string id, string status, int progress, double? score)
        {
            status = (status ?? string.Empty).ToLowerInvariant();

            // Resolve the episode coords to mark watched (series only). Cinemeta's
            // videos carry the real season/episode numbers; order them and take the
            // watched prefix so "watched up to N" lands on the right episodes even
            // across multiple seasons.
            IReadOnlyList<(int Season, int Episode)> episodes = Array.Empty<(int, int)>();
            // Series + "watching": the episode to leave in-progress so Trakt surfaces
            // the show as continue-watching (not completed) — the next unwatched
            // episode after the watched prefix.
            (int Season, int Episode)? inProgress = null;
            if (type == "series" && (status == "watching" || status == "completed"))
            {
                var meta = await _cinemeta.GetVideoMetaAsync("series", id);
                var ordered = (meta?.videos ?? new List<Video>())
                    .OrderBy(v => v.season > 0 ? v.season : 1)
                    .ThenBy(v => v.episode)
                    .Select(v => (Season: v.season > 0 ? v.season : 1, Episode: v.episode))
                    .ToList();
                var take = status == "completed" ? ordered.Count : Math.Clamp(progress, 0, ordered.Count);
                episodes = ordered.Take(take).ToList();
                if (status == "watching" && ordered.Count > 0)
                    inProgress = ordered[Math.Min(take, ordered.Count - 1)];
            }

            int? rating = score.HasValue && score.Value > 0 ? (int)Math.Round(score.Value) : null;
            return await _traktService.SaveVideoEntryAsync(uid, type, id, status, episodes, rating, inProgress);
        }

        // Canonical / per-service status → Trakt's video status vocabulary. Empty /
        // unknown maps to "" (remove from list / not tracked). Mirrors
        // MetaController.MapToTraktStatus.
        private static string MapToTraktStatus(string status) => NormalizeListStatus(status) switch
        {
            "watching" => "watching",
            "completed" => "completed",
            "planning" => "planning",
            "paused" => "onhold",
            "dropped" => "dropped",
            "rewatching" => "rewatching",
            _ => string.Empty,
        };

        /// <summary>
        /// Resolves a detail-page anime id into the primary provider's id-space so
        /// the entry can be read / written. Native ids already in the primary's
        /// service (anilist:/mal:/kitsu:) pass through unchanged; cross-service ids
        /// (imdb:/tmdb:/another provider) are translated through the shared mapping
        /// table — same call the MVC AnimeDetail action uses for its user-state
        /// panel. Returns null when the mapping has no entry for that pair (the
        /// anime genuinely isn't on the user's primary catalog).
        /// </summary>
        private async Task<string> ResolvePrimaryEntryIdAsync(string id, AnimeService service, int? season)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var nativePrefix = service switch
            {
                AnimeService.Anilist => anilistPrefix,
                AnimeService.MyAnimeList => malPrefix,
                AnimeService.Kitsu => kitsuPrefix,
                _ => null,
            };
            if (nativePrefix != null && id.StartsWith(nativePrefix, StringComparison.Ordinal))
                return id;
            return await _mappingService.GetIdWithPrefixAsync(id, service, season);
        }

        // ── Manage Entry season resolution ──────────────────────────────────────
        // Ported from MVC MetaController.BuildSeasonsAsync (+ its helpers) so the
        // Entry endpoint can surface the per-cour Season dropdown for franchises
        // reached via a cross-service id. The Entry path always passes videos:null /
        // episode:null (the modal picks a cour manually), so the auto-episode-select
        // branch is dormant here; it's kept for parity with the source method.

        /// <summary>
        /// Builds the per-entry "Season" dropdown options. Returns:
        ///   - <c>seasons</c>: empty for anilist:/kitsu: ids (no dropdown), or one option per
        ///     mapping for IMDb / TMDB ids that have ≥ 2 mappings.
        ///   - <c>selectedEntryId</c>: the service-prefixed id (anilist:N / kitsu:N) of the
        ///     mapping auto-selected from the URL's season + episode, or the original id if
        ///     no mapping resolution is needed.
        ///   - <c>autoEpisode</c>: the episode number *within* the auto-selected cour.
        /// </summary>
        private async Task<(List<EntrySeason>, string selectedEntryId, int? autoEpisode)>
            BuildSeasonsAsync(string id, bool isSeries, AnimeService animeService, object videos, int? season, int? episode)
        {
            // Single-anime ids: no dropdown, fetch/save against the original id.
            if (!isSeries || (!id.StartsWith(imdbPrefix) && !id.StartsWith(tmdbPrefix)))
                return ([], id, null);

            var mappings = id.StartsWith(imdbPrefix)
                ? await _mappingService.GetImdbMapping(id)
                : await _mappingService.GetTmdbMapping(id);

            // Filter to mappings that actually have an id for the user's service.
            mappings = mappings
                .Where(m => HasServiceId(m, animeService))
                .OrderBy(m => m.Season ?? int.MaxValue)
                .ThenBy(m => SortKey(m, animeService))
                .ToList();

            if (mappings.Count == 0) return ([], id, null);

            // Single mapping: still resolve to the per-service id (so save flows go through
            // the right anime), but skip the dropdown — there's nothing to pick.
            if (mappings.Count == 1)
                return ([], BuildEntryId(mappings[0], animeService) ?? id, null);

            // Multi-mapping: fan out the anime fetches in parallel so the page doesn't pay
            // for them serially. Each fetch is one GraphQL / REST call against the service.
            var entrySeasons = (await Task.WhenAll(
                mappings.Select(m => BuildEntrySeasonAsync(m, animeService))
            )).Where(s => s != null).ToList();

            // Every per-mapping fetch could have returned null (BuildEntrySeasonAsync swallows
            // failures). Fall back to picking the first mapping's id directly so the page
            // still renders without a dropdown rather than NRE'ing on entrySeasons[0].
            if (entrySeasons.Count == 0)
                return ([], BuildEntryId(mappings[0], animeService) ?? id, null);

            var imdbAbsolute = ComputeAbsoluteEpisode(videos, season, episode);
            var (autoId, autoEpisode) = AutoSelectSeason(entrySeasons, imdbAbsolute);

            return (entrySeasons, autoId ?? entrySeasons[0].Id, autoEpisode);
        }

        private async Task<EntrySeason> BuildEntrySeasonAsync(AnimeIdMapping mapping, AnimeService service)
        {
            var entryId = BuildEntryId(mapping, service);
            if (entryId == null) return null;

            // Lightweight summary — title + episode count only. Avoids the heavy
            // GetAnimeByIdAsync path that would otherwise trigger rate limits when we fan
            // out across every cour of a multi-mapping franchise.
            string name = mapping.Name;
            int? episodeCount = mapping.Episodes;
            var updateMappings = false;
            if (string.IsNullOrEmpty(name) || !episodeCount.HasValue)
            {
                try
                {
                    (name, episodeCount) = service switch
                    {
                        AnimeService.Anilist => await _anilistService.GetAnimeSummaryAsync(entryId),
                        AnimeService.MyAnimeList => await _malService.GetAnimeSummaryAsync(entryId),
                        _ => await _kitsuService.GetAnimeSummaryAsync(entryId),
                    };
                    updateMappings = true;
                    mapping.Name = name;
                    mapping.Episodes = episodeCount;
                }
                catch
                {
                    // Best-effort: a single failed summary still renders the rest of the
                    // dropdown — the failed option just shows the raw id as its label.
                }
            }

            if (updateMappings)
            {
                await _mappingService.EnrichImdbMappings([mapping]);
            }

            if (episodeCount is 0) episodeCount = null;

            var label = string.IsNullOrEmpty(name)
                ? entryId
                : (episodeCount.HasValue ? $"{name} ({episodeCount} ep)" : name);

            return new EntrySeason { Id = entryId, Label = label, TotalEpisodes = episodeCount };
        }

        private static string BuildEntryId(AnimeIdMapping mapping, AnimeService service) => service switch
        {
            AnimeService.Anilist => mapping.AnilistId.HasValue ? $"{anilistPrefix}{mapping.AnilistId}" : null,
            AnimeService.MyAnimeList => mapping.MalId.HasValue ? $"{malPrefix}{mapping.MalId}" : null,
            _ => mapping.KitsuId.HasValue ? $"{kitsuPrefix}{mapping.KitsuId}" : null,
        };

        private static bool HasServiceId(AnimeIdMapping mapping, AnimeService service) => service switch
        {
            AnimeService.Anilist => mapping.AnilistId.HasValue,
            AnimeService.MyAnimeList => mapping.MalId.HasValue,
            _ => mapping.KitsuId.HasValue,
        };

        private static int? SortKey(AnimeIdMapping mapping, AnimeService service) => service switch
        {
            AnimeService.Anilist => mapping.AnilistId,
            AnimeService.MyAnimeList => mapping.MalId,
            _ => mapping.KitsuId,
        };

        /// <summary>
        /// Translates a (URL season, URL episode) pair on a Cinemeta-style flat-numbered
        /// series into an absolute episode index across the whole series (1-based).
        /// Returns null if either value is missing. The Entry endpoint always passes
        /// videos:null, so the unknown-shape branch (return episode) is the live path.
        /// </summary>
        private static int? ComputeAbsoluteEpisode(object videos, int? season, int? episode)
        {
            if (!season.HasValue || !episode.HasValue) return null;

            return videos switch
            {
                List<Video> list => list.Count(v => v.season < season.Value) + episode.Value,
                _ => episode, // unknown / null shape — assume IMDb-flat numbering
            };
        }

        /// <summary>
        /// Walks the season buckets in order and finds the one whose cumulative range
        /// contains the absolute IMDb episode. Returns the matching entry id and the
        /// per-entry episode index, or (null, null) when the absolute episode is beyond
        /// all known buckets so the caller can fall back to the first cour.
        /// </summary>
        private static (string entryId, int? episodeWithinEntry) AutoSelectSeason(List<EntrySeason> seasons, int? imdbAbsoluteEpisode)
        {
            if (!imdbAbsoluteEpisode.HasValue) return (null, null);

            int cumulative = 0;
            foreach (var s in seasons)
            {
                if (!s.TotalEpisodes.HasValue) continue;
                if (imdbAbsoluteEpisode > cumulative && imdbAbsoluteEpisode <= cumulative + s.TotalEpisodes.Value)
                    return (s.Id, imdbAbsoluteEpisode.Value - cumulative);
                cumulative += s.TotalEpisodes.Value;
            }
            return (null, null);
        }

        /// <summary>
        /// The user's movies / series library from Trakt for the active tab — the
        /// header-authed twin of LibraryController.VideoPaneAsync. <paramref name="type"/>
        /// is <c>movie</c> or <c>series</c>; <paramref name="list"/> is
        /// <c>current</c> (Continue Watching / playback) / <c>completed</c> (Watched
        /// history) / <c>planning</c> (Watchlist) / <c>paused</c> (On Hold) /
        /// <c>dropped</c> — the last two ride AniSync-managed Trakt personal lists,
        /// resolved by TraktService.GetListAsync. <paramref name="search"/> applies the
        /// same relevance re-rank (ScoreMatch &gt;= 0.4) the anime list + the MVC
        /// VideoPaneAsync use. Returns hydrated, anime-excluded video Metas. Empty when
        /// Trakt isn't configured or the config has no stored row.
        /// </summary>
        [HttpGet("video-list")]
        [RequireConfig]
        [ProducesResponseType(typeof(MetaListResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> VideoList(string type = "movie", string list = "current", string search = null)
        {
            var cfg = await Utils.ResolveConfigAsync(ResolvedConfig, _configStore);
            var uid = cfg?.tokenUid;
            if (string.IsNullOrEmpty(uid) || !_traktService.IsConfigured)
                return Ok(new MetaListResponse([]));

            var mediaType = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase)
                ? MetaType.series : MetaType.movie;
            // Same tab → Trakt-list mapping the original uses; Paused / Dropped resolve to the
            // AniSync-managed personal lists inside TraktService.GetListAsync (today they fell
            // through to Current here, so On Hold / Dropped silently showed the Watching list).
            var listType = list?.ToLowerInvariant() switch
            {
                "completed" or "watched" => ListType.Completed,
                "planning" or "watchlist" or "planned" => ListType.Planning,
                "paused" or "on_hold" or "onhold" => ListType.Paused,
                "dropped" => ListType.Dropped,
                "rewatching" or "repeating" => ListType.Repeating,
                _ => ListType.Current,
            };

            var items = await _traktService.GetListAsync(uid, listType, mediaType);
            var metas = await items.ToVideoMetas().ExcludeAnimeAsync(_mappingService);

            // Search → relevance-ranked (ScoreMatch >= 0.4), mirroring the anime List action and
            // the MVC VideoPaneAsync; otherwise leave the Trakt order intact.
            if (!string.IsNullOrWhiteSpace(search) && metas.Count > 0)
            {
                var q = Utils.NormalizeTitle(search);
                metas = metas
                    .Select(m => (meta: m, score: Utils.ScoreMatch(q, m.name)))
                    .Where(x => x.score >= 0.4)
                    .OrderByDescending(x => x.score)
                    .Select(x => x.meta)
                    .ToList();
            }

            return Ok(new MetaListResponse(metas));
        }

        /// <summary>
        /// Reads the user's named preferences (season grouping, hide-unaired,
        /// adult content, auto-track). Header-authed twin of the config page's
        /// toggle reads — decodes the stored flag bits server-side so the thin
        /// client never has to know the bit layout. <c>Stored</c> is false for
        /// inline (v3) configs that have no persisted row to edit.
        /// </summary>
        [HttpGet("preferences")]
        [RequireConfig]
        [ProducesResponseType(typeof(PreferencesDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetPreferences()
        {
            try
            {
                var configuration = await ResolveConfigAsync(ResolvedConfig, _configStore);
                var uid = configuration?.tokenUid;

                var cfg = new Configuration();
                if (!string.IsNullOrEmpty(uid))
                {
                    var (f1, f2, f3, _) = await _configStore.GetFlagsAsync(uid);
                    Utils.ApplyBinaryFlags(cfg, f1, f2, f3);
                }

                return new JsonResult(new PreferencesDto
                {
                    GroupSeasons = cfg.enableSeasonGrouping,
                    HideUnaired = cfg.hideUnreleasedFromWatching,
                    ShowAdult = cfg.showAdultContent,
                    DisableAutoTrack = cfg.disableAutoTrack,
                    Stored = !string.IsNullOrEmpty(uid),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API GetPreferences failed.");
                return StatusCode(500, new ApiError("preferences lookup failed"));
            }
        }

        /// <summary>
        /// Persists the user's named preferences. Reads the current flag bits,
        /// flips only the preference bits (leaving the Stremio catalog toggles
        /// untouched), and re-packs them. Requires a stored (v5) config; inline
        /// configs have nowhere to persist to.
        /// </summary>
        [HttpPost("preferences")]
        [RequireConfig]
        [ProducesResponseType(typeof(PreferencesDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SavePreferences([FromBody] PreferencesDto body)
        {
            if (body == null) return BadRequest(new ApiError("missing request body"));
            try
            {
                var configuration = await ResolveConfigAsync(ResolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid))
                    return BadRequest(new ApiError("config is not stored — preferences need a v5 install URL"));

                var cfg = new Configuration();
                var (f1, f2, f3, _) = await _configStore.GetFlagsAsync(uid);
                Utils.ApplyBinaryFlags(cfg, f1, f2, f3);

                cfg.enableSeasonGrouping = body.GroupSeasons;
                cfg.hideUnreleasedFromWatching = body.HideUnaired;
                cfg.showAdultContent = body.ShowAdult;
                cfg.disableAutoTrack = body.DisableAutoTrack;

                var (n1, n2, n3) = Utils.PackBinaryFlags(cfg);
                await _configStore.SetFlagsAsync(uid, n1, n2, n3);

                body.Stored = true;
                return new JsonResult(body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API SavePreferences failed.");
                return StatusCode(500, new ApiError("preferences save failed"));
            }
        }

        /// <summary>
        /// The user's saved dashboard layout (section order + visibility JSON), or null if they
        /// haven't customised it. The thin client merges this with the default layout. Same JSON
        /// shape (<c>[{key,visible}]</c>) the web's /Home/SetDashboardLayout persists, so a layout
        /// carries across both apps.
        /// </summary>
        [HttpGet("dashboard-layout")]
        [RequireConfig]
        [ProducesResponseType(typeof(DashboardLayoutResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetDashboardLayout()
        {
            try
            {
                var configuration = await ResolveConfigAsync(ResolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid)) return new JsonResult(new DashboardLayoutResponse(null));
                var ws = await _configStore.GetWebSettingsAsync(uid);
                return new JsonResult(new DashboardLayoutResponse(ws?.DashboardLayout));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API GetDashboardLayout failed.");
                return StatusCode(500, new ApiError("dashboard-layout lookup failed"));
            }
        }

        /// <summary>Persists the dashboard layout JSON (section order + visibility). Requires a
        /// stored (v5) config; inline configs have nowhere to persist to (no-op).</summary>
        [HttpPost("dashboard-layout")]
        [RequireConfig]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> SetDashboardLayout([FromBody] DashboardLayoutSaveRequest body)
        {
            try
            {
                var configuration = await ResolveConfigAsync(ResolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (!string.IsNullOrEmpty(uid) && body is not null)
                    await _configStore.SetDashboardLayoutAsync(uid, body.Layout);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API SetDashboardLayout failed.");
                return StatusCode(500, new ApiError("dashboard-layout save failed"));
            }
        }

        /// <summary>Preferred default playback languages (audio + subtitle, ISO 639-1). Null fields mean
        /// "English default" — the client treats a missing value as "en".</summary>
        [HttpGet("playback-languages")]
        [RequireConfig]
        [ProducesResponseType(typeof(PlaybackLanguagesDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetPlaybackLanguages()
        {
            try
            {
                var configuration = await ResolveConfigAsync(ResolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid)) return new JsonResult(new PlaybackLanguagesDto(null, null));
                var ws = await _configStore.GetWebSettingsAsync(uid);
                return new JsonResult(new PlaybackLanguagesDto(ws?.DefaultAudioLanguage, ws?.DefaultSubtitleLanguage));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API GetPlaybackLanguages failed.");
                return StatusCode(500, new ApiError("playback-languages lookup failed"));
            }
        }

        /// <summary>Persists the preferred default audio + subtitle languages. Requires a stored (v5)
        /// config; inline configs have nowhere to persist to (no-op).</summary>
        [HttpPost("playback-languages")]
        [RequireConfig]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> SetPlaybackLanguages([FromBody] PlaybackLanguagesDto body)
        {
            try
            {
                var configuration = await ResolveConfigAsync(ResolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (!string.IsNullOrEmpty(uid) && body is not null)
                    await _configStore.SetPlaybackLanguagesAsync(uid, body.Audio, body.Subtitle);
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API SetPlaybackLanguages failed.");
                return StatusCode(500, new ApiError("playback-languages save failed"));
            }
        }

        // ── Watch: episode streams / subtitles / mark-watched / scrobble ──────
        // Header-authed (X-AniSync-Config) twin of the MVC MetaController.Web.cs
        // /meta/* enrichment surface (which is session-authed and filtered out of
        // the web head). The per-addon / bootstrap / AniSkip / mark-watched /
        // scrobble logic is ported faithfully from MetaController; only the auth
        // changes — the user resolves from the config header rather than a session
        // cookie. The subtitle proxy + resolve-stream follower (no per-user auth)
        // live on the public MetaProxyController.

        /// <summary>
        /// Episode source picker data — the header-authed twin of
        /// <c>MetaController.EpisodeStreams</c>. Two modes:
        /// <list type="bullet">
        /// <item>Bootstrap (no <paramref name="addonIndex"/>): returns the list of
        /// configured stream addons (so the client fans out its own per-addon
        /// fetches), the user's external streaming links (gated on the
        /// showExternalStreams toggle), and the episode's AniSkip markers.</item>
        /// <item>Per-addon (<paramref name="addonIndex"/> set): returns the enriched
        /// debrid rows for that one addon (quality / size / seeders / language /
        /// provider / infoHash / isHevc / source / hdr / audio / audioUnsupported /
        /// description).</item>
        /// </list>
        /// For movie-typed entries the season + episode are dropped from the addon
        /// fan-out so the addon's "movie" id shape is used.
        /// </summary>
        [HttpGet("episode-streams")]
        [RequireConfig]
        [ProducesResponseType(typeof(EpisodeStreamsBootstrapResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(EpisodeStreamsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> EpisodeStreams(string id, int? season, int episode, string type = null, int? addonIndex = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest(new ApiError("id required"));

            // For movie-typed entries we pass null episode + null season to the
            // stream-addon fan-out so BuildStremioId emits the "movie" path shape
            // (imdb / kitsu:N alone) instead of the "series" shape (imdb:S:E) — the
            // latter doesn't match anything on the addon side for a feature film.
            var isMovie = string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase);
            int? lookupEpisode = isMovie ? null : episode;
            int? lookupSeason = isMovie ? null : season;

            var resolvedConfig = ResolvedConfig;
            var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
            tokenData ??= new TokenData { anime_service = AnimeService.Kitsu };
            var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);

            // Stream addons — one fan-out per configured manifest URL. Anonymous
            // installs and users with no addons see no debrid sources; the source
            // picker falls back to external links only (if those are enabled).
            var addons = !string.IsNullOrEmpty(uid)
                ? await _configStore.GetStreamAddonsAsync(uid)
                : new List<StreamAddon>();

            // Per-addon mode: the watch page fans out one request per configured
            // addon so streams from the fastest source surface immediately instead
            // of waiting on the slowest addon. This branch skips every bootstrap-only
            // field (externalLinks, skipTimes, addon list) since the client already
            // has them from its initial bootstrap call.
            if (addonIndex.HasValue)
            {
                if (addonIndex.Value < 0 || addonIndex.Value >= addons.Count)
                    return new JsonResult(new EpisodeStreamsResponse(new List<EpisodeStreamDto>()));

                var sourceLinks = await _mappingService.BuildSourceLinksAsync(id);

                // Same ImdbSeason override the legacy bundled path used — a multi-cour
                // franchise's URL season is the AniSync cour-internal value (usually 1);
                // the franchise-side season (>1) must override so the addon queries the
                // right season of the IMDb listing.
                if (!isMovie && lookupSeason.HasValue && lookupSeason.Value > 1)
                    sourceLinks.ImdbSeason = lookupSeason.Value;

                var clientIp = ResolveClientIp(HttpContext);
                var addon = addons[addonIndex.Value];
                var streams = await _addonStreamService.GetStreamsAsync(
                    addon.Url, sourceLinks, lookupSeason, lookupEpisode,
                    tokenData.anime_service, clientIp);

                var labelled = streams.Select(s => new EpisodeStreamDto(
                    Name: s.Name,
                    Title: s.Title,
                    Url: s.Url,
                    Quality: s.Quality,
                    Size: s.Size,
                    Playable: s.Playable,
                    Seeders: s.Seeders,
                    Language: s.Language,
                    Provider: addon.Name,
                    // Torrent hash — the client merge dedups identical releases returned
                    // by more than one addon before applying the 5-per-resolution cap.
                    InfoHash: s.InfoHash,
                    IsHevc: s.IsHevc,
                    Source: s.Source,
                    Hdr: s.Hdr,
                    Audio: s.Audio,
                    // Derived from the already-detected audio label (not in the stream
                    // service, so the shared parse path stays untouched). Dolby/DTS/TrueHD
                    // play as silent video in most browsers — the watch page warns + offers
                    // an external player.
                    AudioUnsupported: IsBrowserUnsupportedAudio(s.Audio),
                    // Raw addon description — the client renders the release title from it
                    // (Stremio's right column) instead of the short name.
                    Description: s.Description)).ToList();

                return new JsonResult(new EpisodeStreamsResponse(labelled));
            }

            // Bootstrap mode: hand the client the list of configured addons plus the
            // addon-independent extras (external links, AniSkip markers).
            var addonList = addons
                .Select((a, i) => new EpisodeStreamAddonDto(i, a.Name))
                .ToList();

            // External streaming destinations — same per-service dispatch
            // StreamController uses. Series-level (no episode deep-link). Gated on the
            // user's showExternalStreams toggle so disabling it hides the Other sites
            // block here too.
            var externalEnabled = false;
            if (!string.IsNullOrEmpty(uid))
            {
                var cfg = await GetConfigByUidAsync(uid, _configStore);
                externalEnabled = cfg?.showExternalStreams == true;
            }
            List<StreamingLink> externalRaw = null;
            if (externalEnabled && !string.IsNullOrEmpty(id))
            {
                externalRaw = tokenData.anime_service switch
                {
                    AnimeService.Anilist     => await _anilistService.GetExternalLinksAsync(id, tokenData),
                    AnimeService.MyAnimeList => await _malService.GetExternalLinksAsync(id, tokenData),
                    _                        => await _kitsuService.GetExternalLinksAsync(id, tokenData),
                };
            }

            var externalLinks = (externalRaw ?? new List<StreamingLink>())
                .Where(l => !string.IsNullOrEmpty(l.Url) && !string.IsNullOrEmpty(l.Site))
                .Select(l => new EpisodeExternalLinkDto(l.Site, l.Url))
                .ToList();

            // AniSkip — same lookup chain StreamController.BuildSkipHintsAsync uses for
            // the Stremio addon side. Returns the intro/outro markers for the resolved
            // MAL id; surfaces silently as null when there's no MAL mapping or markers.
            EpisodeSkipTimesDto skipTimes = null;
            try
            {
                var malIdRaw = await _mappingService.GetIdByService(id, AnimeService.MyAnimeList, season);
                if (int.TryParse(malIdRaw, out var malId) && malId > 0 && episode > 0)
                {
                    var markers = await _aniSkipService.GetSkipTimesAsync(malId, episode);
                    if (markers != null && markers.Count > 0)
                    {
                        // Multiple "op" variants (op / mixed-op) can exist — last-wins
                        // matches what BuildSkipHintsAsync does.
                        SkipTime intro = null, outro = null;
                        foreach (var m in markers)
                        {
                            switch (m.Type)
                            {
                                case "op": case "mixed-op": intro = m; break;
                                case "ed": case "mixed-ed": outro = m; break;
                            }
                        }
                        skipTimes = new EpisodeSkipTimesDto(
                            intro == null ? null : new EpisodeSkipMarkerDto(intro.Start, intro.End),
                            outro == null ? null : new EpisodeSkipMarkerDto(outro.Start, outro.End));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AniSkip lookup failed for {Id} ep {Ep}.", id, episode);
            }

            // Subtitles are fetched lazily by the watch page once the user picks a
            // source — the source's filename is the signal OpenSubtitles needs to
            // return release-matched tracks, and we don't know the pick yet. See
            // EpisodeSubtitles below.
            return new JsonResult(new EpisodeStreamsBootstrapResponse(
                Anonymous: tokenData.anonymousUser,
                AddonsConfigured: addons.Count > 0,
                Addons: addonList,
                ExternalLinks: externalLinks,
                SkipTimes: skipTimes));
        }

        /// <summary>
        /// Lazy subtitle lookup invoked by the watch page after the user picks a
        /// source — the header-authed twin of <c>MetaController.EpisodeSubtitles</c>.
        /// Passing the chosen source's <paramref name="filename"/> to OpenSubtitles is
        /// the signal that selects release-matched subs whose timing matches the file.
        /// Best-effort: any failure returns an empty list so the player initialises
        /// without subs rather than 500ing.
        /// </summary>
        [HttpGet("episode-subtitles")]
        [RequireConfig]
        [ProducesResponseType(typeof(EpisodeSubtitlesResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> EpisodeSubtitles(string id, int? season, int episode, string filename = null, string type = null)
        {
            if (string.IsNullOrWhiteSpace(id) || episode <= 0)
                return new JsonResult(new EpisodeSubtitlesResponse(new List<EpisodeSubtitleDto>(), new EpisodeSubtitleProviderCounts(0)));

            var sourceLinks = await _mappingService.BuildSourceLinksAsync(id);
            if (string.IsNullOrEmpty(sourceLinks.ImdbId))
                // OpenSubtitles is IMDb-keyed via the Stremio addon's series/tt:s:e
                // shape. No IMDb mapping = nothing to ask.
                return new JsonResult(new EpisodeSubtitlesResponse(new List<EpisodeSubtitleDto>(), new EpisodeSubtitleProviderCounts(0)));

            // Movies are keyed by IMDb id alone (movie/tt), not season+episode. The
            // watch page synthesises "episode 1" for the single movie video, so
            // season/episode 0 makes the search take the movie path.
            var isMovie = string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase);

            // ImdbSeason on the mapping is the franchise-side season — same fix as
            // Torrentio. URL season is the AniSync cour-internal value (usually 1).
            var effectiveSeason = isMovie ? null : (sourceLinks.ImdbSeason ?? season);
            var effectiveEpisode = isMovie ? 0 : episode;

            var tracks = await SafeOpenSubtitlesSearch(sourceLinks.ImdbId, effectiveSeason, effectiveEpisode, filename, id);

            return new JsonResult(new EpisodeSubtitlesResponse(
                tracks.Select(t => new EpisodeSubtitleDto(t.Lang, t.Label, t.Url, t.Source)).ToList(),
                // Per-provider counts so the UI can surface a "Subs · OS: X" status chip.
                new EpisodeSubtitleProviderCounts(tracks.Count)));
        }

        private async Task<IReadOnlyList<SubtitleTrack>> SafeOpenSubtitlesSearch(
            string imdbId, int? season, int episode, string filename, string id)
        {
            try { return await _subtitleService.SearchAsync(imdbId, season, episode, filename); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenSubtitles search failed for {Id} ep {Ep}.", id, episode);
                return Array.Empty<SubtitleTrack>();
            }
        }

        /// <summary>
        /// Marks an anime episode watched on the user's primary tracker + linked
        /// secondaries (or routes a movie / series to the user's Trakt history). The
        /// header-authed twin of <c>MetaController.MarkWatched</c>. Honours the
        /// per-user disableAutoTrack opt-out (returns 200 + reason=opted-out), and
        /// the optional source-URL placeholder probe used by the external-launch path.
        /// Reason-coded 200s (no-auth / anonymous / …) rather than alarming 401s so a
        /// no-account viewer's player doesn't surface an error.
        /// </summary>
        [HttpPost("mark-watched")]
        [RequireConfig]
        [ProducesResponseType(typeof(MarkWatchedResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(MarkWatchedResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> MarkWatched([FromBody] ApiMarkWatchedRequest req)
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Id) || req.Episode <= 0)
                return BadRequest(new MarkWatchedResponse(false, "invalid-request"));

            var resolvedConfig = ResolvedConfig;
            var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
            if (tokenData is null || string.IsNullOrWhiteSpace(tokenData.access_token))
                return new JsonResult(new MarkWatchedResponse(false, "no-auth"));
            if (tokenData.anonymousUser)
                return new JsonResult(new MarkWatchedResponse(false, "anonymous"));

            // Honour the per-user "Auto-track progress" toggle (keyed by UID resolved
            // from token identity).
            try
            {
                var (optUid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                if (!string.IsNullOrEmpty(optUid))
                {
                    var cfg = await GetConfigByUidAsync(optUid, _configStore);
                    if (cfg?.disableAutoTrack == true)
                        return new JsonResult(new MarkWatchedResponse(false, "opted-out"));
                }
            }
            catch { /* flag read failed — proceed; better to over-track than miss */ }

            // Optional source verification. The external-launcher trigger sends the
            // source URL it's about to hand off so we can probe it BEFORE persisting
            // the mark — the in-app player has no cold-click duration check.
            if (!string.IsNullOrEmpty(req.SourceUrl))
            {
                if (await LooksLikePlaceholderSourceAsync(req.SourceUrl, HttpContext.RequestAborted))
                {
                    _logger.LogInformation(
                        "Refused mark-watched for {Id} S{Season}E{Episode}: source URL looks like a debrid placeholder.",
                        req.Id, req.Season, req.Episode);
                    return new JsonResult(new MarkWatchedResponse(false, "placeholder"));
                }
            }

            try
            {
                // Video (movie / series) auto-track goes to Trakt: the id is an IMDb tt
                // id, so it bypasses the anime-primary dispatch and lands in the user's
                // Trakt history (primary or linked Trakt token, resolved by uid).
                var isVideo = req.Type == "movie" || req.Type == "series";
                if (isVideo)
                {
                    var (videoUid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                    if (string.IsNullOrEmpty(videoUid))
                        return new JsonResult(new MarkWatchedResponse(false, "no-uid"));

                    var season = req.Type == "series" ? req.Season : null;
                    var episode = req.Type == "series" ? req.Episode : (int?)null;
                    // /scrobble/stop at 100 % both adds to history AND clears the
                    // in-progress playback entry, so a finished title leaves Continue
                    // Watching instead of lingering there.
                    var ok = await _traktService.StopScrobbleAsync(videoUid, req.Type, req.Id, season, episode, 100);
                    return new JsonResult(new MarkWatchedResponse(ok, ok ? null : "trakt-not-connected"));
                }

                // Single call into the shared SyncService helper: dispatches to the
                // right primary-tracker SaveAnimeEntry AND fans out to linked secondaries.
                await _syncService.SaveProgressAndFanOutAsync(tokenData, req.Id, req.Season, req.Episode);
                _logger.LogInformation(
                    "Marked watched: {Id} S{Season}E{Episode} on {Service}.",
                    req.Id, req.Season, req.Episode, tokenData.anime_service);
                return new JsonResult(new MarkWatchedResponse(true));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MarkWatched failed for {Id} S{Season}E{Episode}.",
                    req.Id, req.Season, req.Episode);
                return new JsonResult(new MarkWatchedResponse(false, "save-failed"));
            }
        }

        /// <summary>
        /// In-progress playback for movies / series → Trakt's /scrobble/pause, so the
        /// title surfaces in Continue Watching. The header-authed twin of
        /// <c>MetaController.ScrobbleProgress</c>. Fired by the watch player on
        /// page-leave (beacon) with the current progress %; the &lt;1 % and 95 %+ tail
        /// are filtered client-side (the latter is mark-watched territory). Video-only;
        /// honours the auto-track opt-out.
        /// </summary>
        [HttpPost("scrobble-progress")]
        [RequireConfig]
        [ProducesResponseType(typeof(ScrobbleProgressResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ScrobbleProgressResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ScrobbleProgress([FromBody] ApiScrobbleProgressRequest req)
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Id))
                return BadRequest(new ScrobbleProgressResponse(false, "invalid-request"));
            if (req.Type != "movie" && req.Type != "series")
                return new JsonResult(new ScrobbleProgressResponse(false, "not-video"));
            // Trakt's playback progress is a 0-100 percentage; the client already skips
            // <1 % and the 95 %+ tail (the latter is mark-watched territory).
            if (req.Progress <= 0 || req.Progress >= 100)
                return new JsonResult(new ScrobbleProgressResponse(false, "out-of-range"));

            var resolvedConfig = ResolvedConfig;
            var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
            if (tokenData is null || tokenData.anonymousUser || string.IsNullOrWhiteSpace(tokenData.access_token))
                return new JsonResult(new ScrobbleProgressResponse(false, "no-auth"));

            var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
            if (string.IsNullOrEmpty(uid))
                return new JsonResult(new ScrobbleProgressResponse(false, "no-uid"));

            // Same opt-out the mark-watched hook honours — continue-watching is still
            // tracking, so a user who turned auto-track off gets neither.
            var cfg = await GetConfigByUidAsync(uid, _configStore);
            if (cfg?.disableAutoTrack == true)
                return new JsonResult(new ScrobbleProgressResponse(false, "opted-out"));

            var season = req.Type == "series" ? req.Season : (int?)null;
            var episode = req.Type == "series" ? req.Episode : (int?)null;
            var ok = await _traktService.PauseScrobbleAsync(uid, req.Type, req.Id, season, episode, req.Progress);
            return new JsonResult(new ScrobbleProgressResponse(ok, ok ? null : "trakt-not-connected"));
        }

        // True when the detected audio codec is one no mainstream browser <video> can
        // decode in-page — Dolby Digital / Plus / Atmos, the DTS family, TrueHD. Such a
        // release plays as video with NO sound, so the watch page surfaces a warning +
        // external-player path. Plain string checks against the already-parsed audio
        // label. AAC / MP3 / Opus / FLAC / Vorbis / PCM are not flagged. Ported verbatim
        // from MetaController.IsBrowserUnsupportedAudio.
        private static bool IsBrowserUnsupportedAudio(string audioLabel)
        {
            if (string.IsNullOrEmpty(audioLabel)) return false;
            var a = audioLabel.ToUpperInvariant();
            if (a.Contains("AC3") || a.Contains("AC-3")
                || a.Contains("EAC3") || a.Contains("E-AC-3")
                || a.Contains("DD+") || a.Contains("DDP") || a.Contains("DOLBY")
                || a.Contains("ATMOS") || a.Contains("TRUEHD")
                || a.Contains("DTS"))
                return true;
            // Channel-layout-only label (no codec named). A 6+ channel surround track at
            // these tiers is overwhelmingly a Dolby/DTS stream the browser plays as silent
            // video — flag it UNLESS a browser-safe codec is also named.
            var hasSafeCodec = a.Contains("AAC") || a.Contains("MP3") || a.Contains("OPUS")
                || a.Contains("VORBIS") || a.Contains("FLAC") || a.Contains("PCM");
            if (!hasSafeCodec
                && (a.Contains("8CH") || a.Contains("7CH") || a.Contains("6CH")
                    || a.Contains("7.1") || a.Contains("6.1") || a.Contains("5.1")))
                return true;
            return false;
        }

        /// <summary>
        /// Resolves the user's real client IP for downstream addon requests (so
        /// IP-bound debrid playback tokens sign to the user's IP, not AniSync's
        /// backend). Consults CF / Fly / X-Forwarded-For headers then the connection
        /// remote IP, unwrapping IPv4-mapped IPv6. Ported verbatim from
        /// MetaController.ResolveClientIp.
        /// </summary>
        private static string ResolveClientIp(HttpContext ctx)
        {
            string headerIp = null;
            if (ctx.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf) && cf.Count > 0)
                headerIp = cf[0]?.Trim();
            if (string.IsNullOrEmpty(headerIp)
                && ctx.Request.Headers.TryGetValue("Fly-Client-IP", out var fly) && fly.Count > 0)
                headerIp = fly[0]?.Trim();
            if (string.IsNullOrEmpty(headerIp)
                && ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff) && xff.Count > 0)
                headerIp = xff[0]?.Split(',')[0]?.Trim();

            if (!string.IsNullOrEmpty(headerIp)) return headerIp;

            var addr = ctx.Connection.RemoteIpAddress;
            if (addr == null) return null;
            if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
            return addr.ToString();
        }

        /// <summary>
        /// Probes a source URL with a Range 0-0 request and reports whether the total
        /// file size looks like RD's DMCA placeholder (≤50 MB). Used by MarkWatched's
        /// external-launch path to refuse marking a known-bad source. Best-effort: any
        /// HTTP / network failure returns false (don't block the mark). Ported verbatim
        /// from MetaController.LooksLikePlaceholderSourceAsync.
        /// </summary>
        private async Task<bool> LooksLikePlaceholderSourceAsync(string url, CancellationToken ct)
        {
            const long SuspiciouslySmallBytes = 50 * 1024 * 1024;
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(8));

                var client = _httpClientFactory.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                req.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");
                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);

                long? totalSize = null;
                if (res.Content.Headers.ContentRange?.HasLength == true)
                {
                    totalSize = res.Content.Headers.ContentRange.Length;
                }
                else if (res.StatusCode == System.Net.HttpStatusCode.OK
                         && res.Content.Headers.ContentLength.HasValue)
                {
                    totalSize = res.Content.Headers.ContentLength.Value;
                }
                return totalSize.HasValue && totalSize.Value < SuspiciouslySmallBytes;
            }
            catch
            {
                return false; // probe failed — don't block mark on transient issues
            }
        }
    }

    /// <summary>The saved dashboard layout JSON ([{key,visible}]) or null — GET /api/v1/me/dashboard-layout.</summary>
    public record DashboardLayoutResponse(string? Layout);
    /// <summary>Body for POST /api/v1/me/dashboard-layout — the serialized [{key,visible}] array.</summary>
    public record DashboardLayoutSaveRequest(string? Layout);
    /// <summary>Preferred default playback languages (ISO 639-1) — GET/POST /api/v1/me/playback-languages.
    /// Null means "English default".</summary>
    public record PlaybackLanguagesDto(string? Audio, string? Subtitle);

    /// <summary>Per-user detail-page state — GET /api/v1/me/state/{id}. Drives the
    /// hero's user-state pill, the quick-add heart, and the Hide / Unhide button.
    /// <c>Status</c> is the raw provider value (client normalises); null / 0 / false
    /// when the anime isn't on the user's list or isn't hidden. <c>TraktConnected</c>
    /// is only meaningful for the video (movie / series) branch — it gates the video
    /// status pill + heart the same way the MVC VideoDetail does (false for anime).</summary>
    public record DetailStateResponse(bool OnList, string? Status, int Progress, int? TotalEpisodes, double? Score, bool IsHidden, bool TraktConnected = false);

    /// <summary>Body for POST /api/v1/me/watching/toggle (quick-add heart). Season
    /// is the optional cour for franchise ids; Type ("movie" / "series") routes the
    /// toggle to Trakt for a Cinemeta video (null / empty for anime).</summary>
    public record ToggleEntryRequest(string? Id, int? Season, string? Type = null);

    /// <summary>Result of the quick-add heart toggle. <c>Watching</c> is the new
    /// heart state; <c>Hidden</c> is true when the entry is in another list and the
    /// client should drop the heart entirely (managed via the modal instead).</summary>
    public record ToggleWatchingResponse(bool Ok, bool Watching, bool Hidden);

    /// <summary>Body for POST /api/v1/me/hidden/toggle. Title / ImageUrl / MediaType
    /// are cached at hide time so the Discover Hidden section renders without a
    /// re-fetch.</summary>
    public record ToggleHideRequest(string? Id, string? Title, string? ImageUrl, string? MediaType);

    /// <summary>Result of the Hide / Unhide toggle — <c>Hidden</c> is the new state.</summary>
    public record ToggleHiddenResponse(bool Ok, bool Hidden);

    /// <summary>Named user preferences exposed by GET/POST /api/v1/me/preferences.</summary>
    public sealed class PreferencesDto
    {
        public bool GroupSeasons { get; set; }
        public bool HideUnaired { get; set; }
        public bool ShowAdult { get; set; }
        public bool DisableAutoTrack { get; set; }
        /// <summary>False for inline (v3) configs with no persisted row — read-only.</summary>
        public bool Stored { get; set; }
    }
}
