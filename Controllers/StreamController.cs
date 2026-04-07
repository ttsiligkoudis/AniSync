using AnimeList.Services;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class StreamController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnimeMappingService _animeMapping;

        public StreamController(ITokenService tokenService, IAnimeMappingService animeMapping)
        {
            _tokenService = tokenService;
            _animeMapping = animeMapping;
        }

        [HttpGet("{config}/stream/{type}/{id}.json")]
        public async Task<JsonResult> GetStreams(string config, string type, string id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);

            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token) || tokenData.anonymousUser)
                return new JsonResult(new { streams = Array.Empty<object>() });

            int? season = null;
            int? episode = null;
            string? animeId = null;
            var parts = id.Split(':');

            if (parts.Length >= 1 && id.StartsWith("tt"))
            {
                animeId = parts[0];

                if (parts.Length >= 2 && int.TryParse(parts[^2], out var seasonTmp) && int.TryParse(parts[^1], out var episodeTmp))
                {
                    season = seasonTmp;
                    episode = episodeTmp;
                }
            }
            else if (parts.Length >= 2
                && (id.StartsWith(kitsuPrefix) || id.StartsWith(anilistPrefix) || id.StartsWith(tmdbPrefix)))
            {
                // Kitsu-prefixed ID (e.g. kitsu:12345:1:5) — pass as-is, services handle conversion
                animeId = $"{parts[0]}:{parts[1]}";

                if (parts.Length >= 3 && int.TryParse(parts[^2], out var seasonTmp) && int.TryParse(parts[^1], out var episodeTmp))
                {
                    season = seasonTmp;
                    episode = episodeTmp;
                }
            }

            if (string.IsNullOrEmpty(animeId))
            {
                return new JsonResult(new { streams = Array.Empty<object>() });
            }

            var tempUrl = "";

            if (season.HasValue)
            {
                tempUrl += string.IsNullOrEmpty(tempUrl) ? $"?season={season}" : $"&season={season}";
            }

            if (episode.HasValue)
            {
                tempUrl += string.IsNullOrEmpty(tempUrl) ? $"?episode={episode}" : $"&episode={episode}";
            }

            var manageUrl = $"{Request.Scheme}://{Request.Host}/{config}/Meta/ManageEntry/{animeId}{tempUrl}";

            return new JsonResult(new
            {
                streams = new[]
                {
                    new
                    {
                        title = "\ud83d\udcdd Manage Entry",
                        externalUrl = manageUrl
                    }
                }
            });
        }
    }
}
