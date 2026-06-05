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
        private readonly IConfigStore _configStore;
        private readonly ISyncService _syncService;
        private readonly IAnimeMappingService _mappingService;
        private readonly IUserListCache _listCache;
        private readonly IAnimeScheduleService _scheduleService;
        private readonly IWatchingCacheStore _watchingCache;
        private readonly IHiddenEntryStore _hiddenStore;
        private readonly IMergedListService _mergedListService;
        private readonly ILogger<UserApiController> _logger;

        public UserApiController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            ITraktService traktService,
            IConfigStore configStore,
            ISyncService syncService,
            IAnimeMappingService mappingService,
            IUserListCache listCache,
            IAnimeScheduleService scheduleService,
            IWatchingCacheStore watchingCache,
            IHiddenEntryStore hiddenStore,
            IMergedListService mergedListService,
            ILogger<UserApiController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _traktService = traktService;
            _configStore = configStore;
            _syncService = syncService;
            _mappingService = mappingService;
            _listCache = listCache;
            _scheduleService = scheduleService;
            _watchingCache = watchingCache;
            _hiddenStore = hiddenStore;
            _mergedListService = mergedListService;
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
        /// </summary>
        [HttpGet("entries/{id}")]
        [RequireConfig]
        [ProducesResponseType(typeof(EntryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Entry(string id, int? season = null)
        {
            try
            {
                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

                var entry = tokenData.anime_service switch
                {
                    AnimeService.Anilist => await _anilistService.GetAnimeEntryAsync(tokenData, id, season),
                    AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, id, season),
                    // Trakt: reading a single anime entry back as an AnimeEntry isn't wired up
                    // until the Trakt list integration lands (Phase 3), so report "not on list"
                    // rather than calling Kitsu with a Trakt token. The write path still works.
                    AnimeService.Trakt => null,
                    _ => await _kitsuService.GetAnimeEntryAsync(tokenData, id, season),
                };

                if (entry == null) return NotFound(new ApiError("entry not on user's list"));
                return new JsonResult(new EntryResponse(entry));
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
        [HttpPost("entries/{id}")]
        [RequireConfig]
        [ProducesResponseType(typeof(SaveEntryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SaveEntry(string id, [FromQuery] int? season,
            [FromBody] ApiSaveEntryRequest request)
        {
            try
            {
                if (request == null) return BadRequest(new ApiError("missing request body"));

                var resolvedConfig = ResolvedConfig;
                var tokenData = await _tokenService.GetAccessTokenAsync(resolvedConfig);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new ApiError("config has no primary token"));

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

                // 18+ gate from the user's site preferences. Honored on the
                // external API surface too so a programmatic client respects
                // the same toggle the web app does. ResolvedConfig is the raw
                // header string; ResolveConfigAsync decodes it (v3 inline or
                // v5 store-backed) into the Configuration object that carries
                // the flag bits.
                var configuration = await ResolveConfigAsync(resolvedConfig, _configStore);
                var hideAdult = configuration?.showAdultContent != true;
                var metas = tokenData.anime_service switch
                {
                    AnimeService.Anilist => await _anilistService.GetAnimeListAsync(tokenData, ListType.Current, hideAdult: hideAdult),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, ListType.Current, hideAdult: hideAdult),
                    _ => await _kitsuService.GetAnimeListAsync(tokenData, ListType.Current, hideAdult: hideAdult),
                };

                return new JsonResult(new ContinueWatchingResponse(
                    Primary: tokenData.anime_service.ToString(),
                    Items: (metas ?? []).Take(limit).ToList()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API ContinueWatching failed.");
                return StatusCode(500, new ApiError("continue-watching lookup failed"));
            }
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

                var configuration = await ResolveConfigAsync(resolvedConfig, _configStore);
                var hideAdult = configuration?.showAdultContent != true;
                var hideUnreleased = configuration?.hideUnreleasedFromWatching == true;
                var groupSeasons = configuration?.enableSeasonGrouping == true;

                // Fan out to the primary + every healthy linked secondary (deduped) so the library
                // surfaces anime the user tracks on a linked AniList/MAL/Kitsu too — porting the MVC
                // LibraryController.Page (MergedListService is shared with the dashboard shelf). Falls
                // back to the primary service only when the uid can't be resolved (v3 inline config).
                var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                List<Meta> metas;
                if (!string.IsNullOrEmpty(uid))
                {
                    var merged = await _mergedListService.GetMergedListAsync(
                        tokenData, uid, listType, genre, groupSeasons, hideUnreleased, hideAdult);
                    metas = merged?.ToList() ?? new List<Meta>();
                }
                else
                {
                    var single = tokenData.anime_service switch
                    {
                        AnimeService.Anilist => await _anilistService.GetAnimeListAsync(tokenData, listType, hideAdult: hideAdult),
                        AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(tokenData, listType, hideAdult: hideAdult),
                        _ => await _kitsuService.GetAnimeListAsync(tokenData, listType, hideAdult: hideAdult),
                    };
                    metas = single?.ToList() ?? new List<Meta>();
                }

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
        public async Task<IActionResult> Hidden(string skip = null)
        {
            try
            {
                var configuration = await ResolveConfigAsync(ResolvedConfig, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid)) return new JsonResult(new MetaListResponse([]));

                const int pageSize = 24;
                var offset = int.TryParse(skip, out var s) && s > 0 ? s : 0;
                var hidden = await _hiddenStore.GetPageAsync(uid, pageSize, offset);
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

        /// <summary>
        /// The user's movies / series library from Trakt for the active tab — the
        /// header-authed twin of LibraryController.VideoPaneAsync. <paramref name="type"/>
        /// is <c>movie</c> or <c>series</c>; <paramref name="list"/> is
        /// <c>current</c> (Continue Watching) / <c>completed</c> (Watched) /
        /// <c>planning</c> (Watchlist). Returns hydrated, anime-excluded video Metas.
        /// Empty when Trakt isn't configured or the config has no stored row.
        /// </summary>
        [HttpGet("video-list")]
        [RequireConfig]
        [ProducesResponseType(typeof(MetaListResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> VideoList(string type = "movie", string list = "current")
        {
            var cfg = await Utils.ResolveConfigAsync(ResolvedConfig, _configStore);
            var uid = cfg?.tokenUid;
            if (string.IsNullOrEmpty(uid) || !_traktService.IsConfigured)
                return Ok(new MetaListResponse([]));

            var mediaType = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase)
                ? MetaType.series : MetaType.movie;
            var listType = list?.ToLowerInvariant() switch
            {
                "completed" or "watched" => ListType.Completed,
                "planning" or "watchlist" or "planned" => ListType.Planning,
                _ => ListType.Current,
            };

            var items = await _traktService.GetListAsync(uid, listType, mediaType);
            var metas = await items.ToVideoMetas().ExcludeAnimeAsync(_mappingService);
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
    }

    /// <summary>The saved dashboard layout JSON ([{key,visible}]) or null — GET /api/v1/me/dashboard-layout.</summary>
    public record DashboardLayoutResponse(string? Layout);
    /// <summary>Body for POST /api/v1/me/dashboard-layout — the serialized [{key,visible}] array.</summary>
    public record DashboardLayoutSaveRequest(string? Layout);

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
