using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnimeList.Controllers
{
    // Stremio addon protocol route — open CORS (Stremio fetches it cross-origin)
    // and a generous per-UID rate limit (see the "addon" policy in Program.cs).
    [ApiController]
    [EnableCors("AddonCors")]
    [EnableRateLimiting("addon")]
    public class SubtitlesController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;
        private readonly ISyncService _syncService;
        private readonly ILogger<SubtitlesController> _logger;

        // The per-service tracker writes (AniList / MAL / Kitsu) used
        // to live here, but the auto-track recipe now goes through
        // ISyncService.SaveProgressAndFanOutAsync so it's shared with
        // AnimeController.MarkWatched. Dropping the three direct
        // dependencies keeps the constructor honest about what this
        // controller actually needs.
        public SubtitlesController(
            ITokenService tokenService,
            IConfigStore configStore,
            ISyncService syncService,
            ILogger<SubtitlesController> logger)
        {
            _tokenService = tokenService;
            _configStore = configStore;
            _syncService = syncService;
            _logger = logger;
        }

        [HttpGet("{config}/subtitles/{type}/{id}/{fileName}.json")]
        public async Task<JsonResult> GetSubtitles(string config, string type, string id, string fileName)
        {
            var empty = new JsonResult(new { subtitles = Array.Empty<object>() });
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync(config);

                if (string.IsNullOrWhiteSpace(tokenData?.access_token) || IsTokenExpired(tokenData.expiration_date))
                    return empty;

                // The configure-page "Auto-track progress" toggle persists as the negative bit
                // (disableAutoTrack); when true the user has opted out of subtitle-driven progress
                // saves, so we just return the empty subtitles list and skip the side effect.
                var configuration = await ResolveConfigAsync(config, _configStore);
                if (configuration?.disableAutoTrack == true)
                    return empty;

                // We only persist progress when the request includes season + episode
                if (!TryParseAnimeId(id, out var animeId, out var season, out var episode)
                    || season is null || episode is null)
                    return empty;

                // Save to primary tracker + fan out to linked secondaries.
                // Shared with AnimeController.MarkWatched (web-app 70 % /
                // external-launch triggers) so the anime_service dispatch +
                // fan-out recipe lives in one place. Status is left null so
                // each target preserves its existing entry status (or
                // creates "watching" when the entry is new).
                await _syncService.SaveProgressAndFanOutAsync(tokenData, animeId, season, episode.Value);

                return empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subtitles request failed (id={Id}, type={Type}).", id, type);
                return empty;
            }
        }
    }
}
