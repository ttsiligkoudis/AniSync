using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class StreamController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnimeMappingService _mappingService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;

        public StreamController(ITokenService tokenService, IAnimeMappingService mappingService,
            IAnilistService anilistService, IKitsuService kitsuService)
        {
            _tokenService = tokenService;
            _mappingService = mappingService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
        }

        [HttpGet("{config}/stream/{type}/{id}.json")]
        public async Task<JsonResult> GetStreams(string config, string type, string id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);
            var configuration = DecodeConfig(config);
            var empty = new JsonResult(new { streams = Array.Empty<object>() });

            if (!TryParseAnimeId(id, out var animeId, out var season, out var episode))
                return empty;

            var streams = new List<object>();

            // Manage Entry stream — always shown by default for authenticated, non-anonymous users
            if (tokenData != null && !string.IsNullOrWhiteSpace(tokenData.access_token) && !tokenData.anonymousUser)
            {
                var query = string.Concat(
                    season.HasValue ? $"?season={season}" : "",
                    episode.HasValue ? (season.HasValue ? $"&episode={episode}" : $"?episode={episode}") : ""
                );
                var manageUrl = $"{Request.Scheme}://{Request.Host}/{config}/Meta/ManageEntry/{animeId}{query}";

                streams.Add(new
                {
                    title = "📝 Manage Entry",
                    externalUrl = manageUrl,
                });
            }

            // External streaming destinations (Crunchyroll, Netflix, …) are opt-in via config
            if (configuration?.showExternalStreams == true)
            {
                var animeService = tokenData?.anime_service ?? AnimeService.Kitsu;
                var resolvedAnimeId = await _mappingService.GetIdByService(animeId, animeService);
                if (!string.IsNullOrEmpty(resolvedAnimeId))
                {
                    var externalLinks = animeService == AnimeService.Anilist
                        ? await _anilistService.GetExternalLinksAsync(animeId, tokenData)
                        : await _kitsuService.GetExternalLinksAsync(animeId, tokenData);

                    // Group all episodes of the same anime so Stremio can advance through them as a binge
                    var bingeGroup = $"anisync:{animeService}:{resolvedAnimeId}";

                    foreach (var link in externalLinks)
                    {
                        streams.Add(new
                        {
                            name = link.Site,
                            title = $"Watch on {link.Site}",
                            externalUrl = link.Url,
                            behaviorHints = new { bingeGroup },
                        });
                    }
                }
            }

            return new JsonResult(new { streams });
        }
    }
}
