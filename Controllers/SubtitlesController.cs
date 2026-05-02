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

        public SubtitlesController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
        }

        [HttpGet("{config}/subtitles/{type}/{id}/{fileName}.json")]
        public async Task<JsonResult> GetSubtitles(string config, string type, string id, string fileName)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);
            var empty = new JsonResult(new { subtitles = Array.Empty<object>() });

            if (string.IsNullOrWhiteSpace(tokenData?.access_token) || IsTokenExpired(tokenData.expiration_date))
                return empty;

            // We only persist progress when the request includes season + episode
            if (!TryParseAnimeId(id, out var animeId, out var season, out var episode)
                || season is null || episode is null)
                return empty;

            if (tokenData.anime_service == AnimeService.Anilist)
                await _anilistService.SaveAnimeEntryAsync(tokenData, animeId, season, episode.Value);
            else
                await _kitsuService.SaveAnimeEntryAsync(tokenData, animeId, season, episode.Value);

            return empty;
        }
    }
}
