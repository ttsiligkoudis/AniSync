using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace AnimeList.Controllers
{
    public class MetaController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly ITmdbService _tmdbService;
        private readonly ICinemetaService _cinemetaService;
        private readonly IAnimeMappingService _mappingService;
        private readonly IHttpClientFactory _clientFactory;

        public MetaController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService, ITmdbService tmdbService, ICinemetaService cinemetaService, IAnimeMappingService mappingService, IHttpClientFactory clientFactory)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _tmdbService = tmdbService;
            _cinemetaService = cinemetaService;
            _mappingService = mappingService;
            _clientFactory = clientFactory;
        }

        private async Task<(dynamic?, bool)> GetByIDInternal(string config, string id, bool deserialize = false)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);

            dynamic result = null;

            if (id.StartsWith(imdbPrefix))
                result = await _cinemetaService.GetAnimeByIdAsync(config, id, Request);

            if (result != null)
            {
                if (deserialize) result = DeserializeObject<dynamic>((string)result).meta;
                return (result, !deserialize);
            }

            if (id.StartsWith(tmdbPrefix)) 
                result = await _tmdbService.GetAnimeByIdAsync(id, tokenData);
            else if (id.StartsWith(kitsuPrefix))
                result = await _kitsuService.GetAnimeByIdAsync(id, tokenData);
            else if (id.StartsWith(anilistPrefix))
                result = await _anilistService.GetAnimeByIdAsync(id, tokenData);

            if (result != null && !string.IsNullOrWhiteSpace(tokenData?.access_token) && !tokenData.anonymousUser)
            {
                var manageUrl = $"{Request.Scheme}://{Request.Host}/{config}/Meta/ManageEntry/{id}";
                result.links.Add(new Link { name = "Manage Entry", category = "Manage", url = manageUrl });
            }

            return (result, false);
        }

        [HttpGet("{config}/[controller]/{metaType}/{id}.json")]
        public async Task<IActionResult> GetByID(string config, MetaType metaType, string id)
        {
            (var anime, var serialized) = await GetByIDInternal(config, id);

            return serialized ? Content(anime, "application/json") : new JsonResult(new { meta = anime });
        }

        [HttpGet("{config}/[controller]/ManageEntry/{*id}")]
        public async Task<IActionResult> ManageEntry(string config, string id, int? season = null, int? episode = null)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);

            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token))
                return View("ManageEntry", new ManageEntryViewModel { Id = id, Config = config });

            var animeService = tokenData.anime_service;

            (var anime, var serialized) = await GetByIDInternal(config, id, true);

            var isSeries = anime?.type == MetaType.series.ToString();

            // Get available seasons from mapping data
            var seasons = isSeries ? await _mappingService.GetSeasonsAsync(id) : [];

            // Default to first season
            var selectedSeason = seasons.Count > 0 ? season ?? seasons[0] : (int?)null;

            // Fetch the user's entry for this anime + season
            var entry = animeService == AnimeService.Anilist
                ? await _anilistService.GetAnimeEntryAsync(tokenData, id, selectedSeason)
                : await _kitsuService.GetAnimeEntryAsync(tokenData, id, selectedSeason);

            var totalEpisodes = entry?.TotalEpisodes;

            if (anime?.videos is List<Video>)
            {
                var videos = anime.videos as List<Video> ?? [];
                totalEpisodes ??= videos.Count(w => !selectedSeason.HasValue || w.season == selectedSeason);
            }
            else if (anime?.videos is JArray)
            {
                var videos = anime?["videos"] as JArray ?? [];
                totalEpisodes ??= videos.Count(w => !selectedSeason.HasValue || w["season"]?.ToString() == selectedSeason.ToString());
            }

            var model = new ManageEntryViewModel
            {
                Id = id,
                Config = config,
                Name = anime?.name ?? "Unknown",
                Poster = anime?.poster,
                Type = anime?.type ?? MetaType.series.ToString(),
                Status = entry?.Status,
                Progress = entry?.Progress ?? episode ?? 0,
                TotalEpisodes = totalEpisodes,
                Seasons = seasons,
                SelectedSeason = selectedSeason,
                AnimeService = animeService,
                Videos = anime?.videos
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
                await _anilistService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress, request.Status);
            else
                await _kitsuService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress, request.Status);

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

