using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class SubtitlesController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly IConfigStore _configStore;
        private readonly ISyncService _syncService;

        public SubtitlesController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService, IMalService malService, IConfigStore configStore, ISyncService syncService)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
            _syncService = syncService;
        }

        [HttpGet("{config}/subtitles/{type}/{id}/{fileName}.json")]
        public async Task<JsonResult> GetSubtitles(string config, string type, string id, string fileName)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);
            var empty = new JsonResult(new { subtitles = Array.Empty<object>() });

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

            switch (tokenData.anime_service)
            {
                case AnimeService.Anilist:
                    await _anilistService.SaveAnimeEntryAsync(tokenData, animeId, season, episode.Value);
                    break;
                case AnimeService.MyAnimeList:
                    await _malService.SaveAnimeEntryAsync(tokenData, animeId, season, episode.Value);
                    break;
                default:
                    await _kitsuService.SaveAnimeEntryAsync(tokenData, animeId, season, episode.Value);
                    break;
            }

            // Mirror the auto-tracked progress to linked secondary providers. Status is left
            // null so each target service preserves its existing entry status (or creates a
            // sensible default when the entry is new — the per-service default is "watching").
            await _syncService.FanOutSaveAsync(tokenData, animeId, season, episode.Value);

            return empty;
        }
    }
}
