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
        private readonly IAnimeMappingService _mappingService;

        public SubtitlesController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService, IAnimeMappingService mappingService)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _mappingService = mappingService;
        }

        [HttpGet("{config}/subtitles/{type}/{id}/{fileName}.json")]
        public async Task<JsonResult> GetSubtitles(string config, string type, string id, string fileName)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);

            if (!string.IsNullOrWhiteSpace(tokenData?.access_token) && !IsTokenExpired(tokenData.expiration_date))
            {
                var parts = id.Split(':');
                if (parts.Length >= 3
                    && parts[0].StartsWith("tt")
                    && int.TryParse(parts[^2], out var season)
                    && int.TryParse(parts[^1], out var episode))
                {
                    var animeId = parts[0];

                    if (tokenData.anime_service == AnimeService.Anilist)
                    {
                        await _anilistService.SaveAnimeEntryAsync(tokenData, animeId, season, episode);
                    }
                    else
                    {
                        await _kitsuService.SaveAnimeEntryAsync(tokenData, animeId, season, episode);
                    }
                }
                else if (parts.Length >= 3
                    && (id.StartsWith(kitsuPrefix) || id.StartsWith(anilistPrefix) || id.StartsWith(tmdbPrefix))
                    && int.TryParse(parts[^2], out season)
                    && int.TryParse(parts[^1], out episode))
                {
                    // Kitsu-prefixed ID (e.g. kitsu:12345:1:5) — pass as-is, services handle conversion
                    var animeId = $"{parts[0]}{parts[1]}";

                    if (tokenData.anime_service == AnimeService.Anilist)
                    {
                        await _anilistService.SaveAnimeEntryAsync(tokenData, animeId, season, episode);
                    }
                    else
                    {
                        await _kitsuService.SaveAnimeEntryAsync(tokenData, animeId, season, episode);
                    }
                }
            }

            return new JsonResult(new { subtitles = Array.Empty<object>() });
        }
    }
}
