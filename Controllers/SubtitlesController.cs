using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

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
        private readonly IMemoryCache _dedupCache;
        private readonly ILogger<SubtitlesController> _logger;

        // Stremio occasionally fires the subtitles request twice in quick
        // succession for the same playback (route reattachments, second
        // language request, browser prefetch). 30s is comfortably longer
        // than the worst observed duplicate gap and short enough that a
        // legitimate next-episode write right after still goes through.
        private static readonly TimeSpan AutoTrackDedupWindow = TimeSpan.FromSeconds(30);

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
            IMemoryCache dedupCache,
            ILogger<SubtitlesController> logger)
        {
            _tokenService = tokenService;
            _configStore = configStore;
            _syncService = syncService;
            _dedupCache = dedupCache;
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

                // Short dedup window to absorb the duplicate-fetch case where Stremio
                // fires the subtitles request twice in quick succession. Key on
                // (user, animeId, season, episode) so each unique playback is allowed
                // through once per window. Fallback identity for inline-token (v3)
                // installs uses the token's user_id / username so anonymous-like
                // entries still get a stable bucket rather than colliding.
                var dedupIdentity = configuration?.tokenUid
                    ?? tokenData.user_id
                    ?? tokenData.username
                    ?? string.Empty;
                var dedupKey = $"subtitles-autotrack:{dedupIdentity}:{animeId}:{season}:{episode}";
                if (_dedupCache.TryGetValue(dedupKey, out _))
                    return empty;
                _dedupCache.Set(dedupKey, true, AutoTrackDedupWindow);

                // Monotone-progress guard. Stremio fires this hook for whatever the
                // user is currently watching — a rewatch of an earlier episode (say
                // ep 4 of a series they're already on ep 8 of) would otherwise rewind
                // the tracker. Read the primary's current progress and skip the save
                // when the new episode isn't a strict advance. New entries (no entry
                // on the primary yet → null) still write through.
                var currentProgress = await _syncService.GetCurrentProgressAsync(tokenData, animeId, season);
                if (currentProgress.HasValue && episode.Value <= currentProgress.Value)
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
