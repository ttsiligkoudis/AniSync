using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class MetaController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;

        public MetaController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
        }

        [HttpGet("{config}/[controller]/{metaType}/{id}.json")]
        public async Task<JsonResult> GetByID(string config, MetaType metaType, string id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);
            var animeService = tokenData?.anime_service ?? AnimeService.Kitsu;

            if (!string.IsNullOrWhiteSpace(tokenData?.access_token) && IsTokenExpired(tokenData.expiration_date))
            {
                return new JsonResult(new { meta = ExpiredMeta() });
            }

            var anime = animeService == AnimeService.Anilist
                ? await _anilistService.GetAnimeByIdAsync(id, tokenData)
                : await _kitsuService.GetAnimeByIdAsync(id, tokenData);

            return new JsonResult(new { meta = anime });
        }
    }
}

