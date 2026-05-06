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
        private readonly ILogger<UserApiController> _logger;

        public UserApiController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IConfigStore configStore,
            ISyncService syncService,
            ILogger<UserApiController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
            _syncService = syncService;
            _logger = logger;
        }

        /// <summary>
        /// Full library export from the primary provider. Returns every list entry
        /// (every status) with progress, score, notes, dates and a service-prefixed
        /// media id. Useful for backup tooling or migrating to a custom client.
        /// </summary>
        [HttpGet("library")]
        public async Task<IActionResult> Library(string config)
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

                return new JsonResult(new
                {
                    primary = tokenData.anime_service.ToString(),
                    entries,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Library failed (config={Config}).", config);
                return StatusCode(500, new { error = "library lookup failed" });
            }
        }

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
