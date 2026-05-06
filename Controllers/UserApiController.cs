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
        private readonly ILogger<UserApiController> _logger;

        public UserApiController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IConfigStore configStore,
            ILogger<UserApiController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
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
