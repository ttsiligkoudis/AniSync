using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    public class MetaController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly ITmdbService _tmdbService;
        private readonly IAnimeMappingService _mappingService;

        public MetaController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService, ITmdbService tmdbService, IAnimeMappingService mappingService)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _tmdbService = tmdbService;
            _mappingService = mappingService;
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

            var anime = id.Contains(tmdbPrefix)
                ? await _tmdbService.GetAnimeByIdAsync(id, tokenData)
                : (animeService == AnimeService.Anilist
                    ? await _anilistService.GetAnimeByIdAsync(id, tokenData)
                    : await _kitsuService.GetAnimeByIdAsync(id, tokenData));

            if (anime != null && !string.IsNullOrWhiteSpace(tokenData?.access_token) && !tokenData.anonymousUser)
            {
                var manageUrl = $"{Request.Scheme}://{Request.Host}/{config}/Meta/ManageEntry/{id}";
                anime.links.Add(new Link { name = "Manage Entry", category = "Manage", url = manageUrl });
            }

            return new JsonResult(new { meta = anime });
        }

        [HttpGet("{config}/[controller]/ManageEntry/{*id}")]
        public async Task<IActionResult> ManageEntry(string config, string id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);

            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token))
                return View("ManageEntry", new ManageEntryViewModel { Id = id, Config = config });

            var animeService = tokenData.anime_service;

            // Fetch anime metadata to show image/title
            var anime = id.Contains(tmdbPrefix)
                ? await _tmdbService.GetAnimeByIdAsync(id, tokenData)
                : (animeService == AnimeService.Anilist
                    ? await _anilistService.GetAnimeByIdAsync(id, tokenData)
                    : await _kitsuService.GetAnimeByIdAsync(id, tokenData));

            var isSeries = anime?.type == MetaType.series.ToString();

            // Get available seasons from mapping data
            var seasons = isSeries ? await _mappingService.GetSeasonsAsync(id) : [];

            // Default to first season
            var selectedSeason = seasons.Count > 0 ? seasons[0] : (int?)null;

            // Fetch the user's entry for this anime + season
            var entry = animeService == AnimeService.Anilist
                ? await _anilistService.GetAnimeEntryAsync(tokenData, id, selectedSeason)
                : await _kitsuService.GetAnimeEntryAsync(tokenData, id, selectedSeason);

            var model = new ManageEntryViewModel
            {
                Id = id,
                Config = config,
                Name = anime?.name ?? "Unknown",
                Poster = anime?.poster,
                Type = anime?.type ?? MetaType.series.ToString(),
                Status = entry?.Status,
                Progress = entry?.Progress ?? 0,
                TotalEpisodes = entry?.TotalEpisodes,
                Seasons = seasons,
                SelectedSeason = selectedSeason,
                AnimeService = animeService
            };

            return View("ManageEntry", model);
        }

        [HttpGet("{config}/[controller]/GetEntry")]
        public async Task<JsonResult> GetEntry(string config, string id, int? season)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);

            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token))
                return new JsonResult(new { success = false });

            var entry = tokenData.anime_service == AnimeService.Anilist
                ? await _anilistService.GetAnimeEntryAsync(tokenData, id, season)
                : await _kitsuService.GetAnimeEntryAsync(tokenData, id, season);

            return new JsonResult(new
            {
                success = true,
                status = entry?.Status,
                progress = entry?.Progress ?? 0,
                totalEpisodes = entry?.TotalEpisodes
            });
        }

        [HttpPost("{config}/[controller]/SaveEntry")]
        public async Task<JsonResult> SaveEntry(string config, [FromBody] SaveEntryRequest request)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);

            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token))
                return new JsonResult(new { success = false });

            if (tokenData.anime_service == AnimeService.Anilist)
                await _anilistService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Status, request.Progress);
            else
                await _kitsuService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Status, request.Progress);

            return new JsonResult(new { success = true });
        }
    }

    public class SaveEntryRequest
    {
        public string Id { get; set; }
        public int? Season { get; set; }
        public string Status { get; set; }
        public int Progress { get; set; }
    }
}

