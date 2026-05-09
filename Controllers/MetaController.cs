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
        private readonly IMalService _malService;
        private readonly ITmdbService _tmdbService;
        private readonly ICinemetaService _cinemetaService;
        private readonly IAnimeMappingService _mappingService;
        private readonly ISyncService _syncService;
        private readonly IFillerListService _fillerListService;
        private readonly IConfigStore _configStore;
        private readonly ILogger<MetaController> _logger;

        public MetaController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService, IMalService malService, ITmdbService tmdbService, ICinemetaService cinemetaService, IAnimeMappingService mappingService, ISyncService syncService, IFillerListService fillerListService, IConfigStore configStore, ILogger<MetaController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _tmdbService = tmdbService;
            _cinemetaService = cinemetaService;
            _mappingService = mappingService;
            _syncService = syncService;
            _fillerListService = fillerListService;
            _configStore = configStore;
            _logger = logger;
        }

        private async Task<(dynamic?, bool)> GetByIDInternal(string config, string id, bool deserialize = false)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);
            // Mirror CatalogController so meta.id stays in the same id space the
            // catalog emitted — clicking through to a card opens the matching detail.
            var configuration = await ResolveConfigAsync(config, _configStore);
            var groupSeasons = configuration?.disableSeasonGrouping != true;

            dynamic result = null;

            if (id.StartsWith(imdbPrefix))
                result = await _cinemetaService.GetAnimeByIdAsync(config, id, Request);

            if (result != null)
            {
                if (deserialize) result = DeserializeObject<dynamic>((string)result).meta;
                return (result, !deserialize);
            }

            // Translate cross-service ids that the user's chosen service can handle natively.
            // For mal: ids we always need to translate (no MalService for non-MAL users); for
            // anilist:/kitsu: ids we leave them alone — the dispatch below picks the right
            // service per-prefix and the cross-service fallback inside MetaController handles
            // mismatches.
            if (id.StartsWith(malPrefix))
            {
                var service = tokenData?.anime_service ?? AnimeService.Kitsu;
                if (service != AnimeService.MyAnimeList)
                {
                    var resolved = await _mappingService.GetIdByService(id, service);
                    if (string.IsNullOrEmpty(resolved)) return (null, false);
                    id = (service == AnimeService.Anilist ? anilistPrefix : kitsuPrefix) + resolved;
                }
            }

            if (id.StartsWith(tmdbPrefix))
                result = await _tmdbService.GetAnimeByIdAsync(id, tokenData);
            else if (id.StartsWith(kitsuPrefix))
            {
                result = await _kitsuService.GetAnimeByIdAsync(id, tokenData, groupSeasons);

                // Cross-service fallback: Kitsu can return null for entries that exist in
                // Stremio's catalogs but are missing / age-restricted / wrong-id on Kitsu's
                // API. If we have an AniList equivalent in the mapping, render meta from
                // there instead of bouncing the user to "No metadata was found".
                if (result == null)
                {
                    var mapping = await _mappingService.GetKitsuMapping(id);
                    if (mapping?.AnilistId != null)
                        result = await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons);
                }
            }
            else if (id.StartsWith(anilistPrefix))
            {
                result = await _anilistService.GetAnimeByIdAsync(id, tokenData, groupSeasons);

                // Symmetric fallback for AniList primary failures.
                if (result == null)
                {
                    var mapping = await _mappingService.GetAnilistMapping(id);
                    if (mapping?.KitsuId != null)
                        result = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons);
                }
            }
            else if (id.StartsWith(malPrefix))
            {
                result = await _malService.GetAnimeByIdAsync(id, tokenData, groupSeasons);

                // Same fallback shape as Kitsu/AniList — if MAL has nothing for the id,
                // try whichever sister service has a mapping so the page still renders.
                if (result == null)
                {
                    var mapping = await _mappingService.GetMalMapping(id);
                    if (mapping?.AnilistId != null)
                        result = await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons);
                    else if (mapping?.KitsuId != null)
                        result = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons);
                }
            }

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
            try
            {
                (var anime, var serialized) = await GetByIDInternal(config, id);

                // Enrich the response's videos[] with filler/canon labels from
                // AnimeFillerList. The two render paths take slightly different shapes —
                // cinemeta hands us a serialised JSON string (so we round-trip parse →
                // mutate → re-serialise), per-service paths hand us a Meta we can mutate
                // in place. Both ultimately call the same FillerListService.
                if (anime != null)
                {
                    if (serialized)
                        anime = await EnrichSerializedWithFillerAsync((string)anime);
                    else
                        await EnrichMetaWithFillerAsync((Meta)anime);
                }

                return serialized ? Content(anime, "application/json") : new JsonResult(new { meta = anime });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Meta GetByID failed (id={Id}, type={MetaType}).", id, metaType);
                // Stremio interprets {meta:null} as "no metadata"; the detail page falls back
                // to the catalog entry's poster + title and stops asking on the same id.
                return new JsonResult(new { meta = (object)null });
            }
        }

        /// <summary>
        /// Mutates a Meta in place: looks up filler categories for each episode and
        /// prefixes the video's title with a coloured emoji. Best-effort — silently
        /// no-ops when the show isn't on AnimeFillerList or the lookup fails.
        /// </summary>
        private async Task EnrichMetaWithFillerAsync(Meta meta)
        {
            if (meta == null || string.IsNullOrEmpty(meta.name) || meta.videos == null || meta.videos.Count == 0)
                return;

            var categories = await _fillerListService.GetEpisodeCategoriesAsync(meta.name);
            if (categories.Count == 0) return;

            foreach (var video in meta.videos)
            {
                if (!categories.TryGetValue(video.episode, out var category)) continue;
                var prefix = FillerPrefix(category);
                if (!string.IsNullOrEmpty(prefix))
                    video.title = prefix + (video.title ?? string.Empty);
            }
        }

        /// <summary>
        /// Same enrichment but operating on the raw cinemeta JSON string. Parses to
        /// JObject, mutates videos[].title, returns the re-serialised JSON. Returns
        /// the input unchanged on any error so a parser blow-up never breaks the
        /// regular meta response.
        /// </summary>
        private async Task<string> EnrichSerializedWithFillerAsync(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            try
            {
                var obj = JObject.Parse(json);
                var meta = obj["meta"] as JObject;
                var name = (string)meta?["name"];
                var videos = meta?["videos"] as JArray;
                if (string.IsNullOrEmpty(name) || videos == null || videos.Count == 0) return json;

                var categories = await _fillerListService.GetEpisodeCategoriesAsync(name);
                if (categories.Count == 0) return json;

                foreach (var v in videos.OfType<JObject>())
                {
                    var episode = (int?)v["episode"];
                    if (!episode.HasValue) continue;
                    if (!categories.TryGetValue(episode.Value, out var category)) continue;
                    var prefix = FillerPrefix(category);
                    if (string.IsNullOrEmpty(prefix)) continue;
                    var existing = (string)v["title"] ?? string.Empty;
                    v["title"] = prefix + existing;
                }

                return obj.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return json;
            }
        }

        private static string FillerPrefix(string category) => category switch
        {
            "canon" => "🟦 ",
            "filler" => "🟨 ",
            "mixed" => "🟧 ",
            _ => string.Empty,
        };

        [HttpGet("{config}/[controller]/ManageEntry/{*id}")]
        public async Task<IActionResult> ManageEntry(string config, string id, int? season = null, int? episode = null)
        {
            try
            {
                return await ManageEntryInternal(config, id, season, episode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManageEntry render failed (id={Id}, season={Season}, episode={Episode}).", id, season, episode);
                // Render the unauthenticated/empty view so the user gets a graceful
                // "nothing to manage" page rather than a stack trace.
                return View("ManageEntry", new ManageEntryViewModel { Id = id, Config = config });
            }
        }

        private async Task<IActionResult> ManageEntryInternal(string config, string id, int? season, int? episode)
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
            var entry = animeService switch
            {
                AnimeService.Anilist => await _anilistService.GetAnimeEntryAsync(tokenData, selectedEntryId, null),
                AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, selectedEntryId, null),
                _ => await _kitsuService.GetAnimeEntryAsync(tokenData, selectedEntryId, null),
            };

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
                .Where(m => HasServiceId(m, animeService))
                .OrderBy(m => m.Season ?? int.MaxValue)
                .ThenBy(m => SortKey(m, animeService))
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

            var mappings = string.IsNullOrEmpty(mapping.ImdbId) ? [] : await _mappingService.GetImdbMapping(mapping.ImdbId);

            // Lightweight summary — title + episode count only. Avoids the heavy
            // GetAnimeByIdAsync path (which pulls categories, episodes include, AniList
            // recommendations, etc.) that would otherwise trigger rate limits when we
            // fan out across every cour of a multi-mapping franchise.
            string name = mapping.Name;
            int? episodeCount = mapping.Episodes;
            var updateMappings = false;
            if (string.IsNullOrEmpty(name) || !episodeCount.HasValue)
            {
                try
                {
                    (name, episodeCount) = service switch
                    {
                        AnimeService.Anilist => await _anilistService.GetAnimeSummaryAsync(entryId),
                        AnimeService.MyAnimeList => await _malService.GetAnimeSummaryAsync(entryId),
                        _ => await _kitsuService.GetAnimeSummaryAsync(entryId),
                    };
                    updateMappings = true;
                    mapping.Name = name;
                    mapping.Episodes = episodeCount;
                }
                catch
                {
                    // Best-effort: a single failed summary still renders the rest of the
                    // dropdown — the failed option just shows the raw id as its label.
                }
            }

            if (updateMappings)
            {
                await _mappingService.EnrichImdbMappings([mapping]);
            }

            if (episodeCount is 0) episodeCount = null;

            var label = string.IsNullOrEmpty(name)
                ? entryId
                : (episodeCount.HasValue ? $"{name} ({episodeCount} ep)" : name);

            return new EntrySeason { Id = entryId, Label = label, TotalEpisodes = episodeCount };
        }

        private static string BuildEntryId(AnimeIdMapping mapping, AnimeService service) => service switch
        {
            AnimeService.Anilist => mapping.AnilistId.HasValue ? $"{anilistPrefix}{mapping.AnilistId}" : null,
            AnimeService.MyAnimeList => mapping.MalId.HasValue ? $"{malPrefix}{mapping.MalId}" : null,
            _ => mapping.KitsuId.HasValue ? $"{kitsuPrefix}{mapping.KitsuId}" : null,
        };

        private static bool HasServiceId(AnimeIdMapping mapping, AnimeService service) => service switch
        {
            AnimeService.Anilist => mapping.AnilistId.HasValue,
            AnimeService.MyAnimeList => mapping.MalId.HasValue,
            _ => mapping.KitsuId.HasValue,
        };

        private static int? SortKey(AnimeIdMapping mapping, AnimeService service) => service switch
        {
            AnimeService.Anilist => mapping.AnilistId,
            AnimeService.MyAnimeList => mapping.MalId,
            _ => mapping.KitsuId,
        };

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
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync(config);

                if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token))
                    return new JsonResult(new { success = false });

                var entry = tokenData.anime_service switch
                {
                    AnimeService.Anilist => await _anilistService.GetAnimeEntryAsync(tokenData, id, season),
                    AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, id, season),
                    _ => await _kitsuService.GetAnimeEntryAsync(tokenData, id, season),
                };

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetEntry failed (id={Id}, season={Season}).", id, season);
                return new JsonResult(new { success = false });
            }
        }

        [HttpPost("{config}/[controller]/SaveEntry")]
        public async Task<JsonResult> SaveEntry(string config, [FromBody] SaveEntryRequest request)
        {
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync(config);

                if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.access_token))
                    return new JsonResult(new { success = false });

                // Empty status = the "None" option in the UI = "remove from list".
                if (string.IsNullOrEmpty(request.Status))
                {
                    switch (tokenData.anime_service)
                    {
                        case AnimeService.Anilist:
                            await _anilistService.DeleteAnimeEntryAsync(tokenData, request.Id, request.Season);
                            break;
                        case AnimeService.MyAnimeList:
                            await _malService.DeleteAnimeEntryAsync(tokenData, request.Id, request.Season);
                            break;
                        default:
                            await _kitsuService.DeleteAnimeEntryAsync(tokenData, request.Id, request.Season);
                            break;
                    }
                    // Mirror the delete to every linked secondary account. Best-effort: per-target
                    // failures inside SyncService are swallowed so the primary delete still wins.
                    await _syncService.FanOutDeleteAsync(tokenData, request.Id, request.Season);
                    return new JsonResult(new { success = true });
                }

                DateTime? startedAt = ParseDate(request.StartedAt);
                DateTime? finishedAt = ParseDate(request.FinishedAt);

                switch (tokenData.anime_service)
                {
                    case AnimeService.Anilist:
                        await _anilistService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress,
                            request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                    case AnimeService.MyAnimeList:
                        await _malService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress,
                            request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                    default:
                        await _kitsuService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress,
                            request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                }

                // Sync the same change to linked secondary providers. SyncService normalises status
                // and score, fans the writes out concurrently, and silently drops mapping gaps so
                // a partial-coverage anime doesn't fail the user's save.
                await _syncService.FanOutSaveAsync(tokenData, request.Id, request.Season, request.Progress,
                    request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveEntry failed (id={Id}, season={Season}, status={Status}).",
                    request?.Id, request?.Season, request?.Status);
                return new JsonResult(new { success = false });
            }
        }

        private static DateTime? ParseDate(string s) =>
            DateTime.TryParse(s, out var dt) ? dt : null;

        // Session-based variants of GetEntry / SaveEntry used by the web-app's
        // inline Manage Entry modal. Identical body to the config-scoped routes
        // above — just resolve the user from the session cookie instead of the
        // path-segment config UID. The modal renders on /library / /discover /
        // dashboard, none of which carry a config in their URL, so a session-
        // auth variant is the natural fit. The existing config-scoped routes
        // stay intact for the standalone Manage Entry page that Stremio's
        // addon flow links to.

        [HttpGet("/api/library/entry")]
        public async Task<JsonResult> GetEntryByApi(string id, int? season)
        {
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync();
                if (tokenData == null || tokenData.anonymousUser ||
                    string.IsNullOrWhiteSpace(tokenData.access_token))
                    return new JsonResult(new { success = false, error = "not-authenticated" });

                var entry = tokenData.anime_service switch
                {
                    AnimeService.Anilist     => await _anilistService.GetAnimeEntryAsync(tokenData, id, season),
                    AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, id, season),
                    _                        => await _kitsuService.GetAnimeEntryAsync(tokenData, id, season),
                };

                return new JsonResult(new
                {
                    success = true,
                    // Service is included so the modal's status dropdown can pick the
                    // right per-provider option set (AniList/MAL/Kitsu use different
                    // status enum values). Sent as the integer enum value so client-
                    // side switch statements stay compact.
                    service = (int)tokenData.anime_service,
                    status = entry?.Status,
                    progress = entry?.Progress ?? 0,
                    totalEpisodes = entry?.TotalEpisodes,
                    score = entry?.Score,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetEntryByApi failed (id={Id}, season={Season}).", id, season);
                return new JsonResult(new { success = false, error = "exception" });
            }
        }

        [HttpPost("/api/library/entry/save")]
        public async Task<JsonResult> SaveEntryByApi([FromBody] SaveEntryRequest request)
        {
            try
            {
                var tokenData = await _tokenService.GetAccessTokenAsync();
                if (tokenData == null || tokenData.anonymousUser ||
                    string.IsNullOrWhiteSpace(tokenData.access_token))
                    return new JsonResult(new { success = false, error = "not-authenticated" });

                // Empty status = "remove from list" — same semantics as the page version.
                if (string.IsNullOrEmpty(request.Status))
                {
                    switch (tokenData.anime_service)
                    {
                        case AnimeService.Anilist:
                            await _anilistService.DeleteAnimeEntryAsync(tokenData, request.Id, request.Season);
                            break;
                        case AnimeService.MyAnimeList:
                            await _malService.DeleteAnimeEntryAsync(tokenData, request.Id, request.Season);
                            break;
                        default:
                            await _kitsuService.DeleteAnimeEntryAsync(tokenData, request.Id, request.Season);
                            break;
                    }
                    await _syncService.FanOutDeleteAsync(tokenData, request.Id, request.Season);
                    return new JsonResult(new { success = true });
                }

                var startedAt = ParseDate(request.StartedAt);
                var finishedAt = ParseDate(request.FinishedAt);

                switch (tokenData.anime_service)
                {
                    case AnimeService.Anilist:
                        await _anilistService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress,
                            request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                    case AnimeService.MyAnimeList:
                        await _malService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress,
                            request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                    default:
                        await _kitsuService.SaveAnimeEntryAsync(tokenData, request.Id, request.Season, request.Progress,
                            request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);
                        break;
                }

                // Same fan-out to linked secondary providers as the config-scoped path.
                await _syncService.FanOutSaveAsync(tokenData, request.Id, request.Season, request.Progress,
                    request.Status, request.Score, request.Notes, request.RewatchCount, startedAt, finishedAt);

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveEntryByApi failed (id={Id}, season={Season}, status={Status}).",
                    request?.Id, request?.Season, request?.Status);
                return new JsonResult(new { success = false, error = "exception" });
            }
        }
    }

    public class SaveEntryRequest
    {
        // All reference-type fields nullable so ASP.NET's [ApiController] auto-validation
        // doesn't treat them as required just because the project has Nullable=enable.
        // Callers (Manage Entry form, /api/v1 entries POST, the browser extension) only
        // send the fields the user actually edited; the rest stay null.
        public string? Id { get; set; }
        public int? Season { get; set; }
        public string? Status { get; set; }
        public int Progress { get; set; }
        public double? Score { get; set; }
        public string? Notes { get; set; }
        public int? RewatchCount { get; set; }
        public string? StartedAt { get; set; }
        public string? FinishedAt { get; set; }
    }
}

