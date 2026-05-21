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
        private readonly IAnimeMappingService _mappingService;
        private readonly ISyncService _syncService;
        private readonly IConfigStore _configStore;
        private readonly IUserListCache _listCache;
        private readonly IAnilistFallback _anilistFallback;
        private readonly IAnimeMetaLoader _animeMetaLoader;
        private readonly ILogger<MetaController> _logger;

        public MetaController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            IAnimeMappingService mappingService,
            ISyncService syncService,
            IConfigStore configStore,
            IUserListCache listCache,
            IAnilistFallback anilistFallback,
            IAnimeMetaLoader animeMetaLoader,
            ILogger<MetaController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _mappingService = mappingService;
            _syncService = syncService;
            _configStore = configStore;
            _listCache = listCache;
            _anilistFallback = anilistFallback;
            _animeMetaLoader = animeMetaLoader;
            _logger = logger;
        }

        /// <summary>
        /// Loads + fully enriches a Meta for the Stremio addon's meta
        /// endpoint. Pipeline:
        ///   1. <see cref="IAnimeMetaLoader.LoadAsync"/> — shared with the
        ///      web app's detail page (per-service dispatch, cross-service
        ///      fallback, imdb-grouped Cinemeta synthesis, tmdb dispatch,
        ///      adult gate, cour renumbering, filler labels, source-link
        ///      map, AniList airing-schedule overlay).
        ///   2. Synchronous extras enrichment — same Sidedata buckets the
        ///      web app's /extras endpoint serves to the client async, but
        ///      Stremio meta is one-shot so we merge them in here.
        ///   3. Surface-specific decoration — supplementary-link URL
        ///      rewrites to /discover routes + Manage Entry link for
        ///      authenticated viewers.
        /// </summary>
        private async Task<Meta> GetByIDInternal(string config, string id)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);
            // Mirror CatalogController so meta.id stays in the same id
            // space the catalog emitted — clicking through to a card
            // opens the matching detail.
            var configuration = await ResolveConfigAsync(config, _configStore);
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var showAdultContent = configuration?.showAdultContent == true;

            var loadResult = await _animeMetaLoader.LoadAsync(id, tokenData, groupSeasons, showAdultContent);
            var anime = loadResult.Anime;
            if (anime == null) return null;

            // Restore the franchise-side season number for Stremio's UI
            // on per-cour entries. The loader's NormaliseCourEpisodeNumbering
            // collapses every video to season=1 so the web app's
            // /watch/{ep} routing and single-tab episode UI work, but
            // Stremio renders meta.videos[].season directly in its season
            // selector — leaving it at 1 makes a Re:Zero S4 page show as
            // "Season 1". NormaliseCour preserves the original season on
            // v.imdbSeason, so each video knows its franchise season
            // even when the cross-service mapping row's Season column
            // happens to be missing (sourceLinks.ImdbSeason can be null
            // for entries the mapping author didn't populate; v.imdbSeason
            // is fed directly from Cinemeta's per-cour data). Falls back
            // to sourceLinks.ImdbSeason for any video whose imdbSeason
            // didn't land (streamingEpisodes-fallback path, where
            // imdbEpisode was already non-null and NormaliseCour skipped
            // the imdbSeason copy). Grouped-imdb renders are untouched —
            // NormaliseCour skips them, so their original season numbers
            // are still in v.season.
            if (!loadResult.RenderedAsGrouped)
            {
                var franchiseSeasonFallback = loadResult.SourceLinks?.ImdbSeason;
                foreach (var v in anime.videos ?? [])
                {
                    if (v == null) continue;
                    var franchiseSeason = v.imdbSeason ?? franchiseSeasonFallback;
                    if (franchiseSeason is int s && s > 0) v.season = s;
                }
            }

            // Sync extras-equivalent: pull supplementary + related links
            // + recommendation links from the same Sidedata cache the
            // web app's /extras endpoint uses, merge into anime.links so
            // Stremio's chip strip surfaces them. Stremio meta is one-
            // shot (the client can't defer like the web app's JS does),
            // so it has to happen here in the same request.
            await EnrichWithExtrasAsync(
                anime,
                loadResult.SourceLinks,
                tokenData?.anime_service ?? AnimeService.Anilist,
                groupSeasons);

            // Rewrite the AniList-sourced Tag / Studio / Staff URLs to
            // point at AniSync's own /discover routes — same destination
            // the web app's chip clicks land on via InternalHrefFor — so
            // a tap in Stremio opens the franchise's tag / studio / staff
            // catalog on this site instead of bouncing to anilist.co.
            RewriteSupplementaryLinkUrls(anime);

            // Manage Entry: authenticated viewers get a chip routed
            // through the addon's path-config so list operations
            // (status / progress / score) can be done from the Stremio
            // detail page without leaving Stremio.
            if (!string.IsNullOrWhiteSpace(tokenData?.access_token) && !tokenData.anonymousUser)
            {
                var manageUrl = $"{Request.Scheme}://{Request.Host}/{config}/Meta/ManageEntry/{anime.id}";
                anime.links ??= [];
                anime.links.Add(new Link { name = "Manage Entry", category = "Manage", url = manageUrl });
            }

            return anime;
        }

        /// <summary>
        /// Stremio mirror of the web app's <c>/anime/{id}/extras</c>
        /// endpoint — pulls the same Sidedata buckets
        /// (<see cref="IAnilistFallback.GetSupplementaryLinksAsync"/>,
        /// <see cref="IAnilistFallback.GetRelatedLinksAsync"/>,
        /// <see cref="IAnilistFallback.GetRecommendationsAsync"/>) and
        /// merges them into <c>anime.links</c>. The web app fires these
        /// async after page render (separate JSON call to /extras and
        /// the client renders carousels / chip rows); Stremio gets one
        /// meta response so we have to inline it. Dedups by category +
        /// anilistId so per-service builders that already added the
        /// same chips (AnilistService inline emits the full strip
        /// natively, KitsuService also fetches Similar) don't duplicate
        /// after the merge.
        /// </summary>
        private async Task EnrichWithExtrasAsync(Meta anime, AnimeSourceLinks sourceLinks, AnimeService translateTo, bool groupSeasons)
        {
            if (anime == null || sourceLinks?.AnilistId is not int anilistId) return;

            List<Link> supplementary = [];
            List<Link> relatedLinks = [];
            List<Link> recommendationLinks = [];

            // Same skip-related-when-grouped rule the web app's /extras
            // applies — a grouped franchise umbrella already collapses
            // prequel / sequel cours, so the chip strip would just point
            // back to cours included in the umbrella the user is
            // already looking at.
            var supplementaryTask = SafelyFetchAsync(
                () => _anilistFallback.GetSupplementaryLinksAsync(anilistId),
                "GetSupplementaryLinksAsync", anilistId);
            var relatedTask = groupSeasons
                ? Task.FromResult<List<Link>>([])
                : SafelyFetchAsync(
                    () => _anilistFallback.GetRelatedLinksAsync(anilistId, translateTo, groupSeasons),
                    "GetRelatedLinksAsync", anilistId);
            var recommendationsTask = SafelyFetchAsync(
                () => _anilistFallback.GetRecommendationsAsync(anilistId, translateTo, groupSeasons),
                "GetRecommendationsAsync", anilistId);

            await Task.WhenAll(supplementaryTask, relatedTask, recommendationsTask);
            supplementary = await supplementaryTask;
            relatedLinks = await relatedTask;
            recommendationLinks = await recommendationsTask;

            anime.links ??= [];

            // Supplementary categories (Tag / Studio / Staff /
            // director / writer / Artist / Composer / Producer): drop
            // any per-service entries in the same categories so
            // AniList's richer chip strip wins. We only touch these
            // categories — movie credits / recommendations / relations
            // are handled below.
            if (supplementary.Count > 0)
            {
                var ownedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Tag", "Studio", "Staff", "director", "writer", "Artist", "Composer", "Producer",
                };
                anime.links = anime.links
                    .Where(l => l != null && !(l.category != null && ownedCategories.Contains(l.category)))
                    .ToList();
                anime.links.AddRange(supplementary.Where(l => l != null));
            }

            // Sequel / Prequel / Similar: dedup by (category, anilistId)
            // so the AnilistService / KitsuService inline paths' entries
            // don't get duplicated when we refetch from the shared
            // sidedata cache.
            var existingRelKeys = new HashSet<string>(
                anime.links
                    .Where(l => l != null && IsRelationCategory(l.category))
                    .Select(l => $"{l.category}:{l.anilistId}"));
            foreach (var link in relatedLinks.Concat(recommendationLinks))
            {
                if (link == null) continue;
                var key = $"{link.category}:{link.anilistId}";
                if (existingRelKeys.Add(key)) anime.links.Add(link);
            }
        }

        private async Task<List<Link>> SafelyFetchAsync(Func<Task<List<Link>>> fetch, string label, int anilistId)
        {
            try
            {
                return await fetch() ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MetaController {Label} failed for anilist {Id}", label, anilistId);
                return [];
            }
        }

        private static bool IsRelationCategory(string category) =>
            string.Equals(category, "Sequel", StringComparison.Ordinal)
            || string.Equals(category, "Prequel", StringComparison.Ordinal)
            || string.Equals(category, "Similar", StringComparison.Ordinal);

        /// <summary>
        /// Rewrites the URL on every supplementary Link (Tag / Studio /
        /// Staff / director / writer / Artist / Composer / Producer) to
        /// point at AniSync's own /discover/{tag|studio|staff} pages
        /// instead of the upstream anilist.co URLs. Same browse routes
        /// the web app's chip clicks resolve to via InternalHrefFor so
        /// a tap in Stremio opens the franchise's tag / studio / staff
        /// catalog on this site rather than bouncing to anilist.co.
        /// Entries that don't carry an AniList id (e.g. a Studio link
        /// from KitsuService that predates the AniList enrichment, or a
        /// Tag whose mapping lookup failed) keep their stored URL so the
        /// chip still has somewhere to go.
        /// </summary>
        private void RewriteSupplementaryLinkUrls(Meta meta)
        {
            if (meta?.links == null || meta.links.Count == 0) return;

            var origin = $"{Request.Scheme}://{Request.Host}";
            foreach (var link in meta.links)
            {
                if (link == null || string.IsNullOrEmpty(link.category)) continue;
                var rewritten = BuildDiscoverUrlForCategory(origin, link);
                if (!string.IsNullOrEmpty(rewritten)) link.url = rewritten;
            }
        }

        private static string BuildDiscoverUrlForCategory(string origin, Link link)
        {
            if (link == null) return null;
            switch (link.category)
            {
                case "Tag":
                    if (string.IsNullOrEmpty(link.name)) return null;
                    return $"{origin}/discover/tag/{Uri.EscapeDataString(link.name)}";
                case "Studio":
                    return link.anilistId.HasValue
                        ? $"{origin}/discover/studio/{link.anilistId.Value}"
                        : null;
                case "Staff":
                case "director":
                case "writer":
                case "Artist":
                case "Composer":
                case "Producer":
                    return link.anilistId.HasValue
                        ? $"{origin}/discover/staff/{link.anilistId.Value}"
                        : null;
                default:
                    return null;
            }
        }

        [HttpGet("{config}/[controller]/{metaType}/{id}.json")]
        public async Task<IActionResult> GetByID(string config, MetaType metaType, string id)
        {
            try
            {
                var anime = await GetByIDInternal(config, id);
                return new JsonResult(new { meta = anime });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Meta GetByID failed (id={Id}, type={MetaType}).", id, metaType);
                // Stremio interprets {meta:null} as "no metadata"; the
                // detail page falls back to the catalog entry's poster +
                // title and stops asking on the same id.
                return new JsonResult(new { meta = (object)null });
            }
        }

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

            var anime = await GetByIDInternal(config, id);
            var isSeries = anime?.type == MetaType.series.ToString();
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
                    // Primary's cached lists are now stale — drop them so the next dashboard /
                    // library render reflects the delete. Linked-secondary caches are flushed
                    // inside SyncService.FanOutDeleteAsync as each target write succeeds.
                    _listCache.Invalidate(tokenData);
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

                _listCache.Invalidate(tokenData);
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

                var animeService = tokenData.anime_service;

                // Resolve seasons + selected entry id when the click came from a card
                // with a cross-service id (imdb:/tmdb:); for native ids (anilist: /
                // kitsu: / mal:) BuildSeasonsAsync short-circuits with an empty
                // seasons list and the original id. isSeries is passed true because
                // the id-prefix check inside BuildSeasonsAsync is the real gate —
                // movies with imdb/tmdb ids resolve to a single mapping and return
                // empty seasons anyway. videos:null skips auto-episode-selection,
                // which the modal doesn't need (user picks manually if at all).
                var (seasons, selectedEntryId, _) =
                    await BuildSeasonsAsync(id, isSeries: true, animeService, videos: null, season, episode: null);

                var entry = animeService switch
                {
                    AnimeService.Anilist     => await _anilistService.GetAnimeEntryAsync(tokenData, selectedEntryId, null),
                    AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, selectedEntryId, null),
                    _                        => await _kitsuService.GetAnimeEntryAsync(tokenData, selectedEntryId, null),
                };

                // totalEpisodes comes from the entry itself when present; falls back
                // to the matched season's count when the user has no entry yet (so
                // the progress input still shows the right max).
                var totalEpisodes = entry?.TotalEpisodes
                    ?? seasons.FirstOrDefault(s => s.Id == selectedEntryId)?.TotalEpisodes;

                return new JsonResult(new
                {
                    success = true,
                    // Service is included so the modal's status dropdown can pick the
                    // right per-provider option set (AniList/MAL/Kitsu use different
                    // status enum values). Sent as the integer enum value so client-
                    // side switch statements stay compact.
                    service = (int)animeService,
                    selectedEntryId,
                    // Seasons array is empty for non-franchise entries; the modal
                    // hides the dropdown when length < 2 (a single mapping isn't
                    // worth a picker — there's nothing to pick).
                    seasons = seasons.Select(s => new { id = s.Id, label = s.Label, totalEpisodes = s.TotalEpisodes }),
                    status = entry?.Status,
                    progress = entry?.Progress ?? 0,
                    totalEpisodes,
                    score = entry?.Score,
                    notes = entry?.Notes,
                    rewatchCount = entry?.RewatchCount ?? 0,
                    // ISO-8601 yyyy-MM-dd strings so they slot directly into <input type="date">.
                    startedAt = entry?.StartedAt?.ToString("yyyy-MM-dd"),
                    finishedAt = entry?.FinishedAt?.ToString("yyyy-MM-dd"),
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
                    _listCache.Invalidate(tokenData);
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

                _listCache.Invalidate(tokenData);
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

