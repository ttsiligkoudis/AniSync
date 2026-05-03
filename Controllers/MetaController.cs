using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

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

        public MetaController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService, ITmdbService tmdbService, ICinemetaService cinemetaService, IAnimeMappingService mappingService)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _tmdbService = tmdbService;
            _cinemetaService = cinemetaService;
            _mappingService = mappingService;
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

            // Translate mal: ids into the user's chosen service id before dispatching
            if (id.StartsWith(malPrefix))
            {
                var service = tokenData?.anime_service ?? AnimeService.Kitsu;
                var resolved = await _mappingService.GetIdByService(id, service);
                if (string.IsNullOrEmpty(resolved)) return (null, false);
                id = (service == AnimeService.Anilist ? anilistPrefix : kitsuPrefix) + resolved;
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

            (dynamic anime, _) = await GetByIDInternal(config, id, true);
            // Cast .type out of dynamic so isSeries is statically `bool` — otherwise the
            // dynamic flows into BuildSeasonsAsync below and the compiler can't deconstruct
            // its return tuple (CS8133: cannot deconstruct dynamic objects).
            var typeStr = (string)anime?.type;
            var isSeries = typeStr == MetaType.series.ToString();

            // Cast .videos through `object` once so subsequent pattern-matching is statically
            // typed (the `dynamic` from anime would otherwise leak into every is-pattern and
            // break C# type inference, e.g. on the deconstruction below).
            object videosObj = anime?.videos;

            // Build the per-entry season list. For anilist:/kitsu: ids the list is empty
            // (no dropdown needed); for IMDb / TMDB ids it has one EntrySeason per mapping
            // so franchises like Spy x Family that are split across multiple AniList anime
            // surface each cour as its own selectable season with its real episode count.
            (List<EntrySeason> seasons, string selectedEntryId, int? autoEpisode) =
                await BuildSeasonsAsync(id, isSeries, animeService, videosObj, season, episode);

            // Fetch the user's entry against the resolved per-mapping id rather than the
            // original IMDb id — that's what makes the displayed status / progress / score
            // reflect the cour the user picked, not "whichever cour FirstOrDefault picked".
            var entry = animeService == AnimeService.Anilist
                ? await _anilistService.GetAnimeEntryAsync(tokenData, selectedEntryId, null)
                : await _kitsuService.GetAnimeEntryAsync(tokenData, selectedEntryId, null);

            var totalEpisodes = entry?.TotalEpisodes
                ?? seasons.FirstOrDefault(s => s.Id == selectedEntryId)?.TotalEpisodes;
            if (!totalEpisodes.HasValue && videosObj is List<Video> vList)
                totalEpisodes = vList.Count;
            else if (!totalEpisodes.HasValue && videosObj is JArray vArr)
                totalEpisodes = vArr.Count;

            var model = new ManageEntryViewModel
            {
                Id = id,
                Config = config,
                Name = anime?.name ?? "Unknown",
                Poster = anime?.poster,
                Type = anime?.type ?? MetaType.series.ToString(),
                Status = entry?.Status,
                // Prefer the per-entry episode we computed from the IMDb-flat episode
                // (autoEpisode) over the entry's saved progress, so opening Manage Entry at
                // "S1 E39" lands on the right episode of the right cour rather than the
                // user's previously-saved progress for that cour.
                Progress = autoEpisode ?? entry?.Progress ?? episode ?? 0,
                TotalEpisodes = totalEpisodes,
                Score = entry?.Score,
                Notes = entry?.Notes,
                RewatchCount = entry?.RewatchCount ?? 0,
                StartedAt = entry?.StartedAt,
                FinishedAt = entry?.FinishedAt,
                Seasons = seasons,
                SelectedEntryId = selectedEntryId,
                AnimeService = animeService,
                Videos = anime?.videos
            };

            return View("ManageEntry", model);
        }

        /// <summary>
        /// Builds the per-entry "Season" dropdown options. Returns:
        ///   - <c>seasons</c>: empty for anilist:/kitsu: ids (no dropdown), or one option per
        ///     mapping for IMDb / TMDB ids that have ≥ 2 mappings.
        ///   - <c>selectedEntryId</c>: the service-prefixed id (anilist:N / kitsu:N) of the
        ///     mapping auto-selected from the URL's season + episode, or the original id if
        ///     no mapping resolution is needed.
        ///   - <c>autoEpisode</c>: the episode number *within* the auto-selected cour (so
        ///     "S1 E39" of the IMDb-flat series resolves to e.g. cour 4 episode 2).
        /// </summary>
        private async Task<(List<EntrySeason>, string selectedEntryId, int? autoEpisode)>
            BuildSeasonsAsync(string id, bool isSeries, AnimeService animeService, object videos, int? season, int? episode)
        {
            // Single-anime ids: no dropdown, fetch/save against the original id.
            if (!isSeries || (!id.StartsWith(imdbPrefix) && !id.StartsWith(tmdbPrefix)))
                return ([], id, null);

            var mappings = id.StartsWith(imdbPrefix)
                ? await _mappingService.GetImdbMapping(id)
                : await _mappingService.GetTmdbMapping(id);

            // Filter to mappings that actually have an id for the user's service.
            mappings = mappings
                .Where(m => animeService == AnimeService.Anilist ? m.AnilistId.HasValue : m.KitsuId.HasValue)
                .OrderBy(m => m.Season ?? int.MaxValue)
                .ThenBy(m => animeService == AnimeService.Anilist ? m.AnilistId : m.KitsuId)
                .ToList();

            if (mappings.Count == 0) return ([], id, null);

            // Single mapping: still resolve to the per-service id (so save flows go through
            // the right anime), but skip the dropdown — there's nothing to pick.
            if (mappings.Count == 1)
                return ([], BuildEntryId(mappings[0], animeService) ?? id, null);

            // Multi-mapping: fan out the anime fetches in parallel so the page doesn't pay
            // for them serially. Each fetch is one GraphQL / REST call against the service.
            var entrySeasons = (await Task.WhenAll(
                mappings.Select(m => BuildEntrySeasonAsync(m, animeService))
            )).Where(s => s != null).ToList();

            // Every per-mapping fetch could have returned null (BuildEntrySeasonAsync swallows
            // failures). Fall back to picking the first mapping's id directly so the page
            // still renders without a dropdown rather than NRE'ing on entrySeasons[0].
            if (entrySeasons.Count == 0)
                return ([], BuildEntryId(mappings[0], animeService) ?? id, null);

            var imdbAbsolute = ComputeAbsoluteEpisode(videos, season, episode);
            var (autoId, autoEpisode) = AutoSelectSeason(entrySeasons, imdbAbsolute);

            return (entrySeasons, autoId ?? entrySeasons[0].Id, autoEpisode);
        }

        private async Task<EntrySeason> BuildEntrySeasonAsync(AnimeIdMapping mapping, AnimeService service)
        {
            var entryId = BuildEntryId(mapping, service);
            if (entryId == null) return null;

            // Best-effort label fetch: if a single mapping's anime fetch throws (AniList
            // returning a malformed Media payload, transient 502, …) we still want the
            // dropdown to render with the remaining seasons. Fall back to a bare entry
            // option labelled with the raw id so the user can at least pick it.
            Meta anime = null;
            try
            {
                anime = service == AnimeService.Anilist
                    ? await _anilistService.GetAnimeByIdAsync(entryId, null)
                    : await _kitsuService.GetAnimeByIdAsync(entryId, null);
            }
            catch
            {
                // swallow — see comment above
            }

            int? episodeCount = anime?.videos?.Count;
            if (episodeCount is 0) episodeCount = null;

            var name = anime?.name;
            var label = string.IsNullOrEmpty(name)
                ? entryId
                : (episodeCount.HasValue ? $"{name} ({episodeCount} ep)" : name);

            return new EntrySeason { Id = entryId, Label = label, TotalEpisodes = episodeCount };
        }

        private static string BuildEntryId(AnimeIdMapping mapping, AnimeService service) =>
            service == AnimeService.Anilist
                ? (mapping.AnilistId.HasValue ? $"{anilistPrefix}{mapping.AnilistId}" : null)
                : (mapping.KitsuId.HasValue ? $"{kitsuPrefix}{mapping.KitsuId}" : null);

        /// <summary>
        /// Translates a (URL season, URL episode) pair on a Cinemeta-style flat-numbered
        /// series into an absolute episode index across the whole series (1-based). For
        /// "1 season, 48 episodes" series like Spy x Family this is just <paramref name="episode"/>;
        /// for properly multi-season Cinemeta entries it sums episode counts of preceding
        /// seasons. Returns null if either value is missing.
        /// </summary>
        private static int? ComputeAbsoluteEpisode(object videos, int? season, int? episode)
        {
            if (!season.HasValue || !episode.HasValue) return null;

            switch (videos)
            {
                case List<Video> list:
                    return list.Count(v => v.season < season.Value) + episode.Value;
                case JArray arr:
                    return arr.Count(v => (int?)v["season"] < season) + episode.Value;
                default:
                    return episode; // unknown shape — assume IMDb-flat numbering
            }
        }

        /// <summary>
        /// Walks the season buckets in order and finds the one whose cumulative range
        /// contains the absolute IMDb episode. Returns the matching entry id and the
        /// per-entry episode index. If the absolute episode is beyond all known buckets
        /// (e.g. mapping data is stale) returns (null, null) so the caller can fall back.
        /// </summary>
        private static (string? entryId, int? episodeWithinEntry) AutoSelectSeason(List<EntrySeason> seasons, int? imdbAbsoluteEpisode)
        {
            if (!imdbAbsoluteEpisode.HasValue) return (null, null);

            int cumulative = 0;
            foreach (var s in seasons)
            {
                if (!s.TotalEpisodes.HasValue) continue;
                if (imdbAbsoluteEpisode > cumulative && imdbAbsoluteEpisode <= cumulative + s.TotalEpisodes.Value)
                    return (s.Id, imdbAbsoluteEpisode.Value - cumulative);
                cumulative += s.TotalEpisodes.Value;
            }
            return (null, null);
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
                totalEpisodes = entry?.TotalEpisodes,
                score = entry?.Score,
                notes = entry?.Notes,
                rewatchCount = entry?.RewatchCount ?? 0,
                startedAt = entry?.StartedAt?.ToString("yyyy-MM-dd"),
                finishedAt = entry?.FinishedAt?.ToString("yyyy-MM-dd"),
            });
        }

        [HttpPost("{config}/[controller]/SaveEntry")]
        public async Task<JsonResult> SaveEntry(string config, [FromBody] SaveEntryRequest request)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);

            if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token))
                return new JsonResult(new { success = false });

            DateTime? startedAt = ParseDate(request.StartedAt);
            DateTime? finishedAt = ParseDate(request.FinishedAt);

            if (tokenData.anime_service == AnimeService.Anilist)
                await _anilistService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress,
                    request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
            else
                await _kitsuService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress,
                    request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);

            return new JsonResult(new { success = true });
        }

        private static DateTime? ParseDate(string s) =>
            DateTime.TryParse(s, out var dt) ? dt : null;
    }

    public class SaveEntryRequest
    {
        public string Id { get; set; }
        public int? Season { get; set; }
        public string Status { get; set; }
        public int Progress { get; set; }
        public double? Score { get; set; }
        public string Notes { get; set; }
        public int? RewatchCount { get; set; }
        public string StartedAt { get; set; }
        public string FinishedAt { get; set; }
    }
}

