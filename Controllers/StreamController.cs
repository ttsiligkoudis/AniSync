using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class StreamController : ControllerBase
    {
        private readonly ITokenService _tokenService;

        public StreamController(ITokenService tokenService)
        {
            _tokenService = tokenService;
        }

        [HttpGet("{config}/stream/{type}/{id}.json")]
        public async Task<JsonResult> GetStreams(string config, string type, string id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);

            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token) || tokenData.anonymousUser)
                return new JsonResult(new { streams = Array.Empty<object>() });

            // Extract the base anime ID (without season:episode suffix)
            var parts = id.Split(':');
            string animeId;

            if (parts[0].StartsWith("tt"))
            {
                // IMDb: tt1234567 or tt1234567:1:5
                animeId = parts[0];
            }
            else if (id.StartsWith(kitsuPrefix) || id.StartsWith(anilistPrefix) || id.StartsWith(tmdbPrefix))
            {
                // Prefixed: kitsu:1234 or kitsu:1234:1:5
                animeId = $"{parts[0]}:{parts[1]}";
            }
            else
            {
                return new JsonResult(new { streams = Array.Empty<object>() });
            }

            var manageUrl = $"{Request.Scheme}://{Request.Host}/{config}/Meta/ManageEntry/{animeId}";

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
