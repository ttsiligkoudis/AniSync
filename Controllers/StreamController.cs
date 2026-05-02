using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class StreamController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnimeMappingService _mappingService;

        public StreamController(ITokenService tokenService, IAnimeMappingService mappingService)
        {
            _tokenService = tokenService;
            _mappingService = mappingService;
        }

        [HttpGet("{config}/stream/{type}/{id}.json")]
        public async Task<JsonResult> GetStreams(string config, string type, string id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);
            var empty = new JsonResult(new { streams = Array.Empty<object>() });

            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token) || tokenData.anonymousUser)
                return empty;

            if (!TryParseAnimeId(id, out var animeId, out var season, out var episode))
                return empty;

            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, tokenData.anime_service);
            if (string.IsNullOrEmpty(resolvedAnimeId)) return empty;

            var query = string.Concat(
                season.HasValue ? $"?season={season}" : "",
                episode.HasValue ? (season.HasValue ? $"&episode={episode}" : $"?episode={episode}") : ""
            );

            var manageUrl = $"{Request.Scheme}://{Request.Host}/{config}/Meta/ManageEntry/{animeId}{query}";

            return new JsonResult(new
            {
                streams = new[]
                {
                    new
                    {
                        title = "📝 Manage Entry",
                        externalUrl = manageUrl
                    }
                }
            });
        }
    }
}
