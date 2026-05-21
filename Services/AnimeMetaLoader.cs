using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Shared id-to-Meta pipeline backing both AnimeController.Detail (web
    /// detail page) and MetaController.GetByIDInternal (Stremio addon
    /// meta). Folds the dispatch + cross-service fallback + grouped-imdb
    /// synthesis + airing-schedule overlay + filler + adult gate that
    /// used to live duplicated across both controllers.
    /// </summary>
    public class AnimeMetaLoader : IAnimeMetaLoader
    {
        private readonly IKitsuService _kitsuService;
        private readonly IAnilistService _anilistService;
        private readonly IMalService _malService;
        private readonly ITmdbService _tmdbService;
        private readonly ICinemetaService _cinemetaService;
        private readonly IAnimeMappingService _mappingService;
        private readonly IAnilistFallback _anilistFallback;
        private readonly IFillerListService _fillerListService;
        private readonly ILogger<AnimeMetaLoader> _logger;

        public AnimeMetaLoader(
            IKitsuService kitsuService,
            IAnilistService anilistService,
            IMalService malService,
            ITmdbService tmdbService,
            ICinemetaService cinemetaService,
            IAnimeMappingService mappingService,
            IAnilistFallback anilistFallback,
            IFillerListService fillerListService,
            ILogger<AnimeMetaLoader> logger)
        {
            _kitsuService = kitsuService;
            _anilistService = anilistService;
            _malService = malService;
            _tmdbService = tmdbService;
            _cinemetaService = cinemetaService;
            _mappingService = mappingService;
            _anilistFallback = anilistFallback;
            _fillerListService = fillerListService;
            _logger = logger;
        }

        public async Task<AnimeMetaLoadResult> LoadAsync(string id, TokenData tokenData, bool groupSeasons, bool showAdultContent)
        {
            if (string.IsNullOrEmpty(id))
                return new AnimeMetaLoadResult(null, false, false, false, null, null, null);

            tokenData ??= new TokenData { anime_service = AnimeService.Kitsu };
            var animeService = tokenData.anime_service;

            // IMDb-id deep-link → franchise-umbrella render. Aggregates
            // every cour's episodes into one Meta with season-numbered
            // videos so the existing multi-season tab UI in Detail.cshtml
            // lights up. Hero metadata comes from Cinemeta (the franchise
            // umbrella's source of truth). Falls through to the per-
            // service path below when no mapping is found.
            bool isImdbGrouped = id.StartsWith(imdbPrefix);
            bool renderedAsGrouped = false;
            Meta anime = null;
            int? imdbHeadSeason = null;
            int? imdbHeadAnilistId = null;

            try
            {
                if (isImdbGrouped)
                {
                    (anime, imdbHeadSeason, imdbHeadAnilistId) = await BuildGroupedImdbAnimeAsync(id);
                    renderedAsGrouped = anime != null;
                }

                if (anime == null && id.StartsWith(tmdbPrefix))
                    anime = await _tmdbService.GetAnimeByIdAsync(id, tokenData);

                if (anime == null)
                {
                    // Resolve cross-service ids (imdb:/tmdb:) to the user's
                    // primary's native id so we can hit the right per-
                    // service endpoint with rich detail data. Falls back
                    // to first-mapping pick if there's no direct id for
                    // the primary's service.
                    id = await ResolveToServiceIdAsync(id, animeService) ?? id;

                    if (id.StartsWith(kitsuPrefix))
                    {
                        anime = await _kitsuService.GetAnimeByIdAsync(id, tokenData, groupSeasons);
                        if (anime == null)
                        {
                            var mapping = await _mappingService.GetKitsuMapping(id);
                            if (mapping?.AnilistId != null)
                                anime = await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons);
                        }
                    }
                    else if (id.StartsWith(anilistPrefix))
                    {
                        anime = await _anilistService.GetAnimeByIdAsync(id, tokenData, groupSeasons);
                        if (anime == null)
                        {
                            var mapping = await _mappingService.GetAnilistMapping(id);
                            if (mapping?.KitsuId != null)
                                anime = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons);
                        }
                    }
                    else if (id.StartsWith(malPrefix))
                    {
                        anime = await _malService.GetAnimeByIdAsync(id, tokenData, groupSeasons);
                        if (anime == null)
                        {
                            var mapping = await _mappingService.GetMalMapping(id);
                            if (mapping?.AnilistId != null)
                                anime = await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons);
                            else if (mapping?.KitsuId != null)
                                anime = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AnimeMetaLoader.LoadAsync failed (id={Id}).", id);
                return new AnimeMetaLoadResult(null, false, false, false, null, null, null);
            }

            if (anime == null)
                return new AnimeMetaLoadResult(null, false, renderedAsGrouped, false, imdbHeadSeason, imdbHeadAnilistId, null);

            // Adult-content gate. Returns AdultFiltered=true so callers
            // can render the appropriate "not found" surface (web 404 /
            // Stremio {meta:null}) without having to introspect anime
            // fields themselves.
            if (anime.isAdult && !showAdultContent)
                return new AnimeMetaLoadResult(null, true, renderedAsGrouped, false, imdbHeadSeason, imdbHeadAnilistId, null);

            // Per-cour videos renumber to 1..N for the within-cour
            // navigation the detail / watch pages drive on. Skipped for
            // grouped-imdb renders: the multi-season tab UI in
            // Detail.cshtml pivots on the original season numbers from
            // Cinemeta so they have to stay intact.
            if (!renderedAsGrouped) NormaliseCourEpisodeNumbering(anime);

            // Multi-cour grouped = >1 distinct season across the
            // franchise's Cinemeta video list. Drives the hero's entry-
            // pill rendering (generic Manage Entry instead of head-cour-
            // specific status). Single-cour grouped reads as the regular
            // per-cour page.
            bool isMultiSeasonGroup = renderedAsGrouped
                && (anime.videos?.Select(v => v.season > 0 ? v.season : 1).Distinct().Count() ?? 0) > 1;

            // Filler / canon enrichment — coloured emoji prefixes on each
            // video title. Best-effort: failures swallow to the logger and
            // the list renders without prefixes.
            await TryEnrichWithFillerAsync(anime);

            // Cross-service link map keyed off anime.id — whichever
            // service the page resolved against, the same row of the
            // mapping dataset carries the sibling-service ids and the
            // imdb tt id when known. Best-effort: missing mapping → no
            // links rendered, which is the correct degradation for
            // entries outside the curated dataset.
            var sourceLinks = await _mappingService.BuildSourceLinksAsync(anime.id);

            // AniList per-episode airing schedule overlaid onto the
            // Cinemeta-sourced videos so the click gate's isFuture check
            // and displayed date track the same source of truth the
            // notifier dispatcher does. For grouped renders the season
            // filter scopes the overlay to the head cour so we don't
            // paint head-cour airing times onto other seasons' same-
            // numbered episodes (a different broadcast).
            var overlayAnilistId = imdbHeadAnilistId ?? sourceLinks?.AnilistId;
            await OverlayAniListAiringScheduleAsync(anime, overlayAnilistId, imdbHeadSeason);

            return new AnimeMetaLoadResult(
                Anime: anime,
                AdultFiltered: false,
                RenderedAsGrouped: renderedAsGrouped,
                IsMultiSeasonGroup: isMultiSeasonGroup,
                ImdbHeadSeason: imdbHeadSeason,
                ImdbHeadAnilistId: imdbHeadAnilistId,
                SourceLinks: sourceLinks);
        }

        /// <summary>
        /// Builds a "franchise umbrella" Meta for an imdb-id deep-link
        /// straight from Cinemeta — title, synopsis, poster, score, year,
        /// runtime, genres, and the full multi-cour video list all come
        /// from a single Cinemeta meta fetch. Mirrors Stremio's own
        /// catalog rendering for a grouped imdb entry (Cinemeta IS
        /// Stremio's meta provider) and skips the per-service round-trip
        /// — the imdb URL already collapses every cour, so a single-
        /// cour's AniList / MAL / Kitsu metadata wouldn't honestly
        /// describe the franchise anyway.
        ///
        /// Returns the head cour's season + anilist id alongside the
        /// franchise Meta so the airing-schedule overlay can scope to the
        /// right cour. HeadSeason / HeadAnilistId are null when no
        /// AniList mapping exists — the overlay then no-ops and the page
        /// falls back to Cinemeta's released strings for the click gate.
        /// </summary>
        public async Task<(Meta Anime, int? HeadSeason, int? HeadAnilistId)> BuildGroupedImdbAnimeAsync(string imdbId)
        {
            if (string.IsNullOrEmpty(imdbId) || !imdbId.StartsWith(imdbPrefix)) return (null, null, null);

            var anime = await _cinemetaService.GetMetaAsync(imdbId);
            if (anime == null || anime.videos == null || anime.videos.Count == 0) return (null, null, null);

            // Drop season=0 specials / side-stories — Cinemeta's
            // convention is that bonus content sits at season=0 (OVAs,
            // recaps, "Side Story" extras) while canonical cour episodes
            // start at season=1. The franchise umbrella render here
            // mirrors what Stremio's catalog rendering shows, which
            // hides season=0 by default; without this filter the
            // /anime/tt... page surfaces interleaved "Side Story" rows
            // (sortable as (season=0→1, episode=1)) right next to the
            // main S1E1, and the count creeps past what the user thinks
            // of as the franchise's episode total. The per-cour path
            // (GetCourEpisodesAsync) already applies the same filter.
            anime.videos = anime.videos
                .Where(v => v != null && v.season > 0)
                .OrderBy(v => v.season)
                .ThenBy(v => v.episode)
                .ToList();
            if (anime.videos.Count == 0) return (null, null, null);
            anime.episodes = anime.videos.Count;
            // Belt-and-braces: Cinemeta should already stamp meta.id with
            // the imdb tt-id, but force-set in case the upstream payload
            // changes shape — the view-side hrefs depend on this
            // carrying the franchise umbrella id.
            anime.id = imdbId;

            var mappings = await _mappingService.GetImdbMapping(imdbId);
            var head = mappings?
                .OrderBy(m => m.Season ?? int.MaxValue)
                .FirstOrDefault();
            return (anime, head?.Season, head?.AnilistId);
        }

        /// <summary>
        /// For imdb: ids, look up the cross-service mapping and translate
        /// to a service-native id the per-service GetAnimeByIdAsync can
        /// handle. For mal: ids consumed by non-MAL primaries, the same
        /// translation. Other ids pass through unchanged.
        /// </summary>
        public async Task<string> ResolveToServiceIdAsync(string id, AnimeService service)
        {
            if (id.StartsWith(imdbPrefix))
            {
                var mappings = await _mappingService.GetImdbMapping(id);
                var first = mappings.FirstOrDefault();
                if (first == null) return null;
                return BuildServiceId(first, service) ?? id;
            }
            if (id.StartsWith(malPrefix) && service != AnimeService.MyAnimeList)
            {
                return await _mappingService.GetIdWithPrefixAsync(id, service);
            }
            return id;
        }

        private static string BuildServiceId(AnimeIdMapping m, AnimeService service) => service switch
        {
            AnimeService.Anilist     => m.AnilistId.HasValue ? $"{anilistPrefix}{m.AnilistId.Value}" : null,
            AnimeService.MyAnimeList => m.MalId.HasValue ? $"{malPrefix}{m.MalId.Value}" : null,
            AnimeService.Kitsu       => m.KitsuId.HasValue ? $"{kitsuPrefix}{m.KitsuId.Value}" : null,
            _                        => null,
        };

        /// <summary>
        /// Renumbers the videos array to within-cour 1..N. Preserves the
        /// original Cinemeta-style imdbSeason / imdbEpisode pointers on
        /// each video so downstream Torrentio queries still have the
        /// correct franchise-side coordinates. Skipped on grouped-imdb
        /// renders where the multi-season tab UI depends on the original
        /// season numbers staying intact.
        /// </summary>
        public static void NormaliseCourEpisodeNumbering(Meta anime)
        {
            if (anime?.videos == null || anime.videos.Count == 0) return;
            var ordered = anime.videos
                .OrderBy(v => v.season > 0 ? v.season : 1)
                .ThenBy(v => v.episode)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var v = ordered[i];
                var originalEpisode = v.episode;
                // Preserve only when the source numbering looks like it
                // came from Cinemeta — i.e. the video already had a non-
                // zero episode. The streamingEpisodes fallback path in
                // the per-service implementations renumbers to 1..N
                // before reaching us, so its season / episode are
                // AniList-cour values, not IMDb — preserving them would
                // mislead the addon query downstream.
                if (v.episode > 0 && v.imdbEpisode == null)
                {
                    v.imdbSeason = v.season > 0 ? v.season : 1;
                    v.imdbEpisode = v.episode;
                }
                v.season = 1;
                v.episode = i + 1;

                // Cinemeta emits placeholder names like "Episode 43" for
                // not-yet-aired rows that don't have a real title yet.
                // After we renumber, "7. Episode 43" is off-by-one and
                // confusing. Rewrite the placeholder to track the new
                // ordinal — only when the existing name / title is
                // literally "Episode <originalNumber>", so real titles
                // like "Harspiel Concert" stay untouched.
                if (originalEpisode > 0 && originalEpisode != v.episode)
                {
                    var placeholder = $"Episode {originalEpisode}";
                    var rebuilt = $"Episode {v.episode}";
                    if (string.Equals(v.name?.Trim(), placeholder, StringComparison.OrdinalIgnoreCase))
                        v.name = rebuilt;
                    if (string.Equals(v.title?.Trim(), placeholder, StringComparison.OrdinalIgnoreCase))
                        v.title = rebuilt;
                }
            }
            anime.videos = ordered;
        }

        /// <summary>
        /// Overlays AniList's per-episode airingAt timestamps onto the
        /// videos array — keeps the click gate's "is this aired yet?"
        /// check in sync with the notifier dispatcher (which already
        /// uses AniList). For multi-cour grouped renders the season
        /// filter scopes the overlay to a single cour so head-cour
        /// times don't bleed onto later seasons' same-numbered episodes.
        /// </summary>
        public async Task OverlayAniListAiringScheduleAsync(Meta anime, int? anilistId, int? seasonFilter = null)
        {
            if (anime?.videos == null || anime.videos.Count == 0) return;
            if (!anilistId.HasValue || anilistId.Value <= 0) return;

            Dictionary<int, long> schedule;
            try
            {
                schedule = await _anilistFallback.GetAiringScheduleByAnilistIdAsync(anilistId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeMetaLoader.OverlayAniListAiringScheduleAsync failed for anilist {Id}", anilistId);
                return;
            }
            if (schedule == null || schedule.Count == 0) return;

            foreach (var v in anime.videos)
            {
                if (v.episode <= 0) continue;
                if (seasonFilter.HasValue
                    && (v.season > 0 ? v.season : 1) != seasonFilter.Value) continue;
                if (schedule.TryGetValue(v.episode, out var airingAt))
                {
                    v.airingAt = airingAt;
                }
            }
        }

        /// <summary>
        /// Prefixes each video.title with a coloured emoji marking it as
        /// canon / filler / mixed per AnimeFillerList's per-show dataset.
        /// Best-effort: any lookup failure / unknown show is silently a
        /// no-op so the list still renders without prefixes.
        /// </summary>
        private async Task TryEnrichWithFillerAsync(Meta meta)
        {
            try
            {
                if (meta == null || string.IsNullOrEmpty(meta.name) ||
                    meta.videos == null || meta.videos.Count == 0) return;

                var categories = await _fillerListService.GetEpisodeCategoriesAsync(meta.name);
                if (categories.Count == 0) return;

                foreach (var video in meta.videos)
                {
                    if (!categories.TryGetValue(video.episode, out var category)) continue;
                    var prefix = category switch
                    {
                        "canon"  => "🟦 ",
                        "filler" => "🟨 ",
                        "mixed"  => "🟧 ",
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(prefix))
                        video.title = prefix + (video.title ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeMetaLoader: filler enrichment failed for {Name}.", meta?.name);
            }
        }
    }
}
