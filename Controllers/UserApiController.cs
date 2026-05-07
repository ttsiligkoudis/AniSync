using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnimeList.Controllers
{
    /// <summary>
    /// User-scoped read-only API surface (Phase 2). Endpoints take the same
    /// <c>{config}</c> UID the Stremio install URL carries so a non-Stremio client
    /// can read the user's library, single-entry state and linked-account list
    /// without re-implementing the auth flows.
    ///
    /// The UID is the only authentication token. Treat it like a password — anyone
    /// holding the URL can read the corresponding library. The configure page
    /// surfaces a "Rotate URL" button that mints a fresh UID and invalidates the
    /// old one if a URL leaks.
    ///
    /// Writes (Manage Entry, sync fan-out, primary swap) ship in Phase 3 once the
    /// idempotency-key + per-provider error-shape contract is locked.
    /// </summary>
    [ApiController]
    [Route("api/v1/users/{config}")]
    [EnableRateLimiting("api")]
    public class UserApiController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly IConfigStore _configStore;
        private readonly ISyncService _syncService;
        private readonly IAnimeMappingService _mappingService;
        private readonly ILogger<UserApiController> _logger;

        public UserApiController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IConfigStore configStore,
            ISyncService syncService,
            IAnimeMappingService mappingService,
            ILogger<UserApiController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
            _syncService = syncService;
            _mappingService = mappingService;
            _logger = logger;
        }

        /// <summary>
        /// Library export from the primary provider. Returns list entries with
        /// progress, score, notes, dates and a service-prefixed media id. Useful for
        /// backup tooling or migrating to a custom client.
        /// </summary>
        /// <param name="config">Install-URL config (the same UID Stremio's install
        /// URL embeds).</param>
        /// <param name="status">Optional list filter. Accepts the friendly names
        /// <c>watching</c> / <c>current</c>, <c>completed</c>, <c>planning</c> /
        /// <c>planned</c> / <c>plan_to_watch</c>, <c>paused</c> / <c>on_hold</c>,
        /// <c>dropped</c>, <c>rewatching</c> / <c>repeating</c>. Aliases are
        /// case-insensitive and normalised internally so the same name works
        /// regardless of which provider is primary. Omit to return every entry.</param>
        [HttpGet("library")]
        public async Task<IActionResult> Library(string config, string status = null)
        {
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync(config);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new { error = "config has no primary token" });

                var entries = tokenData.anime_service switch
                {
                    AnimeService.Anilist => await _anilistService.GetUserListEntriesAsync(tokenData),
                    AnimeService.MyAnimeList => await _malService.GetUserListEntriesAsync(tokenData),
                    _ => await _kitsuService.GetUserListEntriesAsync(tokenData),
                };

                if (!string.IsNullOrEmpty(status))
                {
                    var requested = NormalizeListStatus(status);
                    if (requested == null)
                        return BadRequest(new { error = "unknown status; expected watching|completed|planning|paused|dropped|rewatching" });
                    entries = entries
                        .Where(e => NormalizeListStatus(e.Status) == requested)
                        .ToList();
                }

                return new JsonResult(new
                {
                    primary = tokenData.anime_service.ToString(),
                    status,
                    entries,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Library failed (config={Config}, status={Status}).", config, status);
                return StatusCode(500, new { error = "library lookup failed" });
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
        public async Task<IActionResult> Entry(string config, string id, int? season = null)
        {
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync(config);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new { error = "config has no primary token" });

                var entry = tokenData.anime_service switch
                {
                    AnimeService.Anilist => await _anilistService.GetAnimeEntryAsync(tokenData, id, season),
                    AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, id, season),
                    _ => await _kitsuService.GetAnimeEntryAsync(tokenData, id, season),
                };

                if (entry == null) return NotFound(new { error = "entry not on user's list" });
                return new JsonResult(new { entry });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Entry failed (config={Config}, id={Id}, season={Season}).", config, id, season);
                return StatusCode(500, new { error = "entry lookup failed" });
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
        [HttpPost("entries/{id}")]
        public async Task<IActionResult> SaveEntry(string config, string id, [FromBody] SaveEntryRequest request)
        {
            try
            {
                if (request == null) return BadRequest(new { error = "missing request body" });

                var tokenData = await _tokenService.GetAccessTokenAsync(config);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new { error = "config has no primary token" });

                var startedAt = ParseDate(request.StartedAt);
                var finishedAt = ParseDate(request.FinishedAt);

                if (string.IsNullOrEmpty(request.Status))
                {
                    // "" status == delete from list. Mirrors MetaController.SaveEntry.
                    switch (tokenData.anime_service)
                    {
                        case AnimeService.Anilist:
                            await _anilistService.DeleteAnimeEntryAsync(tokenData, id, request.Season);
                            break;
                        case AnimeService.MyAnimeList:
                            await _malService.DeleteAnimeEntryAsync(tokenData, id, request.Season);
                            break;
                        default:
                            await _kitsuService.DeleteAnimeEntryAsync(tokenData, id, request.Season);
                            break;
                    }
                    await _syncService.FanOutDeleteAsync(tokenData, id, request.Season);
                    return new JsonResult(new { ok = true, removed = true, primary = tokenData.anime_service.ToString() });
                }

                switch (tokenData.anime_service)
                {
                    case AnimeService.Anilist:
                        await _anilistService.SaveAnimeEntryAsync(tokenData, id, request.Season, request.Progress,
                            request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                    case AnimeService.MyAnimeList:
                        await _malService.SaveAnimeEntryAsync(tokenData, id, request.Season, request.Progress,
                            request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                    default:
                        await _kitsuService.SaveAnimeEntryAsync(tokenData, id, request.Season, request.Progress,
                            request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                }

                await _syncService.FanOutSaveAsync(tokenData, id, request.Season, request.Progress,
                    request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);

                return new JsonResult(new { ok = true, primary = tokenData.anime_service.ToString() });
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "API SaveEntry primary 401 (config={Config}, id={Id}).", config, id);
                return Unauthorized(new { error = "primary token rejected by upstream" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API SaveEntry failed (config={Config}, id={Id}).", config, id);
                return StatusCode(500, new { error = "save failed" });
            }
        }

        /// <summary>
        /// Removes an entry from the primary provider's list and fans the delete out
        /// to every linked secondary. Idempotent — deleting an entry that's not on
        /// the list returns 200.
        /// </summary>
        [HttpDelete("entries/{id}")]
        public async Task<IActionResult> DeleteEntry(string config, string id, int? season = null)
        {
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync(config);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new { error = "config has no primary token" });

                switch (tokenData.anime_service)
                {
                    case AnimeService.Anilist:
                        await _anilistService.DeleteAnimeEntryAsync(tokenData, id, season);
                        break;
                    case AnimeService.MyAnimeList:
                        await _malService.DeleteAnimeEntryAsync(tokenData, id, season);
                        break;
                    default:
                        await _kitsuService.DeleteAnimeEntryAsync(tokenData, id, season);
                        break;
                }
                await _syncService.FanOutDeleteAsync(tokenData, id, season);
                return new JsonResult(new { ok = true, primary = tokenData.anime_service.ToString() });
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "API DeleteEntry primary 401 (config={Config}, id={Id}).", config, id);
                return Unauthorized(new { error = "primary token rejected by upstream" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API DeleteEntry failed (config={Config}, id={Id}).", config, id);
                return StatusCode(500, new { error = "delete failed" });
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
        public async Task<IActionResult> SaveEntriesBulk(string config, [FromBody] List<SaveEntryRequest> entries)
        {
            try
            {
                if (entries == null || entries.Count == 0)
                    return BadRequest(new { error = "request body must be a non-empty array of entries" });

                var tokenData = await _tokenService.GetAccessTokenAsync(config);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new { error = "config has no primary token" });

                var results = new List<object>();
                int ok = 0, failed = 0;

                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry?.Id))
                    {
                        failed++;
                        results.Add(new { id = (string)null, ok = false, error = "entry missing id" });
                        continue;
                    }

                    try
                    {
                        var startedAt = ParseDate(entry.StartedAt);
                        var finishedAt = ParseDate(entry.FinishedAt);

                        if (string.IsNullOrEmpty(entry.Status))
                        {
                            switch (tokenData.anime_service)
                            {
                                case AnimeService.Anilist:
                                    await _anilistService.DeleteAnimeEntryAsync(tokenData, entry.Id, entry.Season);
                                    break;
                                case AnimeService.MyAnimeList:
                                    await _malService.DeleteAnimeEntryAsync(tokenData, entry.Id, entry.Season);
                                    break;
                                default:
                                    await _kitsuService.DeleteAnimeEntryAsync(tokenData, entry.Id, entry.Season);
                                    break;
                            }
                            await _syncService.FanOutDeleteAsync(tokenData, entry.Id, entry.Season);
                            ok++;
                            results.Add(new { id = entry.Id, ok = true, removed = true });
                            continue;
                        }

                        switch (tokenData.anime_service)
                        {
                            case AnimeService.Anilist:
                                await _anilistService.SaveAnimeEntryAsync(tokenData, entry.Id, entry.Season, entry.Progress,
                                    entry.Status, entry.Score, entry.Notes, entry.RewatchCount, startedAt, finishedAt);
                                break;
                            case AnimeService.MyAnimeList:
                                await _malService.SaveAnimeEntryAsync(tokenData, entry.Id, entry.Season, entry.Progress,
                                    entry.Status, entry.Score, entry.Notes, entry.RewatchCount, startedAt, finishedAt);
                                break;
                            default:
                                await _kitsuService.SaveAnimeEntryAsync(tokenData, entry.Id, entry.Season, entry.Progress,
                                    entry.Status, entry.Score, entry.Notes, entry.RewatchCount, startedAt, finishedAt);
                                break;
                        }
                        await _syncService.FanOutSaveAsync(tokenData, entry.Id, entry.Season, entry.Progress,
                            entry.Status, entry.Score, entry.Notes, entry.RewatchCount, startedAt, finishedAt);
                        ok++;
                        results.Add(new { id = entry.Id, ok = true });
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        results.Add(new { id = entry.Id, ok = false, error = ex.Message });
                    }
                }

                return new JsonResult(new
                {
                    primary = tokenData.anime_service.ToString(),
                    ok,
                    failed,
                    total = entries.Count,
                    results,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API SaveEntriesBulk failed (config={Config}).", config);
                return StatusCode(500, new { error = "bulk save failed" });
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
        public async Task<IActionResult> Diff(string config)
        {
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync(config);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new { error = "config has no primary token" });

                var configuration = await ResolveConfigAsync(config, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid))
                    return BadRequest(new { error = "config is not stored — diff requires a v4/v5 install URL" });

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

                    var prefix = l.Service switch
                    {
                        AnimeService.Anilist => anilistPrefix,
                        AnimeService.Kitsu => kitsuPrefix,
                        AnimeService.MyAnimeList => malPrefix,
                        _ => string.Empty,
                    };

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
                _logger.LogError(ex, "API Diff failed (config={Config}).", config);
                return StatusCode(500, new { error = "diff failed" });
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
        public async Task<IActionResult> Promote(string config, AnimeService service, bool force = false)
        {
            try
            {
                var configuration = await ResolveConfigAsync(config, _configStore);
                var uid = configuration?.tokenUid;
                if (string.IsNullOrEmpty(uid))
                    return BadRequest(new { error = "config is not stored — only v4/v5 install URLs can swap primary" });

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
                    return StatusCode(status, new { ok = false, reason });
                }

                return new JsonResult(new { ok = true, primary = newPrimary.anime_service.ToString() });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Promote failed (config={Config}, service={Service}).", config, service);
                return StatusCode(500, new { error = "promote failed" });
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
        public async Task Sync(string config)
        {
            var ct = HttpContext.RequestAborted;

            Response.ContentType = "application/x-ndjson";
            Response.Headers["Cache-Control"] = "no-cache";

            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync(config);
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
                _logger.LogError(ex, "API Sync failed (config={Config}).", config);
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

        /// <summary>
        /// Lists every linked secondary provider for this config plus the
        /// <c>NeedsReauth</c> flag, so a client can show the same status pills the
        /// configure page does without needing the v5 install URL.
        /// </summary>
        [HttpGet("linked")]
        public async Task<IActionResult> Linked(string config)
        {
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync(config);
                if (string.IsNullOrEmpty(tokenData?.access_token))
                    return Unauthorized(new { error = "config has no primary token" });

                // Only v5 (UID-only) URLs have linked tokens — v3/v4 inline URLs can
                // only carry the primary, so the linked list is necessarily empty.
                // ResolveConfigAsync decodes the install URL and pulls tokenUid out of
                // either the inline JSON (v4) or the embedded UID byte segment (v5).
                var configuration = await ResolveConfigAsync(config, _configStore);
                var uid = configuration?.tokenUid;
                var linked = string.IsNullOrEmpty(uid)
                    ? new List<LinkedToken>()
                    : await _configStore.GetLinkedTokensAsync(uid);

                return new JsonResult(new
                {
                    primary = tokenData.anime_service.ToString(),
                    linked = linked.Select(l => new
                    {
                        service = l.Service.ToString(),
                        l.NeedsReauth,
                    }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Linked failed (config={Config}).", config);
                return StatusCode(500, new { error = "linked lookup failed" });
            }
        }
    }
}
