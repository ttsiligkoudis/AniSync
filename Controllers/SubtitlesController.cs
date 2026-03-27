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
                    && int.TryParse(parts[^1], out var episode))
                {
                    var imdbId = parts[0];

                    if (tokenData.anime_service == AnimeService.Anilist)
                    {
                        var anilistId = await _mappingService.GetAnilistIdByImdbIdAsync(imdbId);
                        if (anilistId.HasValue)
                            await _anilistService.UpdateEpisodeProgressAsync(tokenData, $"{anilistPrefix}{anilistId}", episode);
                    }
                    else
                    {
                        var kitsuId = await _mappingService.GetKitsuIdByImdbIdAsync(imdbId);
                        if (kitsuId.HasValue)
                            await _kitsuService.UpdateEpisodeProgressAsync(tokenData, $"{kitsuPrefix}{kitsuId}", episode);
                    }
                }
                else if (parts.Length >= 3
                    && id.StartsWith(kitsuPrefix)
                    && int.TryParse(parts[^1], out var kitsuEpisode))
                {
                    // Kitsu-prefixed ID (e.g. kitsu:12345:1:5) — pass as-is, services handle conversion
                    var kitsuAnimeId = $"{kitsuPrefix}{parts[1]}";

                    if (tokenData.anime_service == AnimeService.Anilist)
                    {
                        await _anilistService.UpdateEpisodeProgressAsync(tokenData, kitsuAnimeId, kitsuEpisode);
                    }
                    else
                    {
                        await _kitsuService.UpdateEpisodeProgressAsync(tokenData, kitsuAnimeId, kitsuEpisode);
                    }
                }
            }

            return new JsonResult(new { subtitles = Array.Empty<object>() });
        }
    }
}
