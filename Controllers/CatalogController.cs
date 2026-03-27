using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class CatalogController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;

        public CatalogController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
        }

        [HttpGet("{config}/[controller]/{metaType}/{listType}/skip={skip:int}.json")]
        public async Task<ActionResult> GetListWithSkip(string config, MetaType metaType, ListType listType, string skip, string animeId = null)
        {
            return await GetList(config, metaType, listType, skip, animeId);
        }

        [HttpGet("{config}/[controller]/{metaType}/{listType}.json")]
        public async Task<ActionResult> GetList(string config, MetaType metaType, ListType listType, string skip = null, string animeId = null)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);
            var animeService = tokenData?.anime_service ?? AnimeService.Kitsu;

            if (!string.IsNullOrWhiteSpace(tokenData?.access_token) && IsTokenExpired(tokenData.expiration_date))
            {
                return new JsonResult(new { metas = ExpiredMetas() });
            }

            var metas = animeService == AnimeService.Anilist
                ? await _anilistService.GetAnimeListAsync(tokenData, listType, skip, animeId)
                : await _kitsuService.GetAnimeListAsync(tokenData, listType, skip, animeId);

            return new JsonResult(new { metas });
        }
    }
}

