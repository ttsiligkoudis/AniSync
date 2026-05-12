using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Web-app detail page for an individual anime. Mirrors what
    /// MetaController.GetByIDInternal does for the Stremio addon, but
    /// session-based (no path-config) and rendering an HTML page rather
    /// than the addon's JSON. Cards across /library / /discover / the
    /// dashboard's Continue Watching shelf all link here on click.
    /// </summary>
    public class AnimeController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly ITmdbService _tmdbService;
        private readonly IAnimeMappingService _mappingService;
        private readonly IConfigStore _configStore;
        private readonly IFillerListService _fillerListService;
        private readonly IAnilistFallback _anilistFallback;
        private readonly ILogger<AnimeController> _logger;

        public AnimeController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            ITmdbService tmdbService,
            IAnimeMappingService mappingService,
            IConfigStore configStore,
            IFillerListService fillerListService,
            IAnilistFallback anilistFallback,
            ILogger<AnimeController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _tmdbService = tmdbService;
            _mappingService = mappingService;
            _configStore = configStore;
            _fillerListService = fillerListService;
            _anilistFallback = anilistFallback;
            _logger = logger;
        }

        // {*id} catches any id shape including the colon-prefixed ones
        // (anilist:123 / kitsu:456 / mal:789 / imdb:tt... / tmdb:...).
        // Without the catch-all the colon would be url-decoded into a
        // route-segment delimiter.
        [Route("/anime/{*id}")]
        public async Task<IActionResult> Detail(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            // Session for personalisation (link badges, Edit button visibility);
            // anonymous fresh-visitors get a Kitsu-default synthetic token like
            // /discover does so the per-service dispatch below has a service
            // to switch on. The detail data itself is public — no auth required
            // to render the page.
            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };
            var animeService = tokenData.anime_service;

            // Resolve the row's UID for logged-in users so the entry-fetch
            // path below can hit the user's tracker with the right identity.
            // Anonymous viewers get null, which is fine — the entry block is
            // gated on !anonymousUser anyway.
            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolvedUid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolvedUid;
            }
            // /anime/{id} is always ungrouped — the enableSeasonGrouping pref
            // now only governs Stremio's catalog / meta endpoints. The detail
            // page is the destination for a single-cour card, so resolving the
            // requested id natively (instead of the IMDb-collapsed franchise
            // id) is what the user expects.
            const bool groupSeasons = false;

            // Resolve cross-service ids (imdb:/tmdb:) to the user's primary's
            // native id so we can hit the right per-service endpoint with rich
            // detail data. Falls back to first-mapping pick if there's no
            // direct id for the primary's service.
            id = await ResolveToServiceIdAsync(id, animeService) ?? id;

            Meta anime = null;
            try
            {
                if (id.StartsWith(tmdbPrefix))
                    anime = await _tmdbService.GetAnimeByIdAsync(id, tokenData);
                else if (id.StartsWith(kitsuPrefix))
                {
                    anime = await _kitsuService.GetAnimeByIdAsync(id, tokenData, groupSeasons: groupSeasons);
                    if (anime == null)
                    {
                        var mapping = await _mappingService.GetKitsuMapping(id);
                        if (mapping?.AnilistId != null)
                            anime = await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons: groupSeasons);
                    }
                }
                else if (id.StartsWith(anilistPrefix))
                {
                    anime = await _anilistService.GetAnimeByIdAsync(id, tokenData, groupSeasons: groupSeasons);
                    if (anime == null)
                    {
                        var mapping = await _mappingService.GetAnilistMapping(id);
                        if (mapping?.KitsuId != null)
                            anime = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons: groupSeasons);
                    }
                }
                else if (id.StartsWith(malPrefix))
                {
                    anime = await _malService.GetAnimeByIdAsync(id, tokenData, groupSeasons: groupSeasons);
                    if (anime == null)
                    {
                        var mapping = await _mappingService.GetMalMapping(id);
                        if (mapping?.AnilistId != null)
                            anime = await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons: groupSeasons);
                        else if (mapping?.KitsuId != null)
                            anime = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons: groupSeasons);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AnimeController.Detail failed (id={Id}).", id);
                Response.StatusCode = 404;
                return View("NotFound");
            }

            if (anime == null)
            {
                // Mapping miss / upstream gone. Hand off to the shared 404
                // page so this matches what users see on any other bad URL.
                Response.StatusCode = 404;
                return View("NotFound");
            }

            // Filler / canon enrichment — same pattern as MetaController's
            // EnrichMetaWithFillerAsync used by the Stremio addon path.
            // Episodes get a coloured emoji prefix (🟦 canon, 🟨 filler,
            // 🟧 mixed) so the user can skip filler at a glance. Best-effort:
            // failures swallow into the standard logger and the list renders
            // without prefixes. Skipped for movie-shaped entries since
            // AnimeFillerList is a per-episode dataset.
            await TryEnrichWithFillerAsync(anime);

            // uid was already resolved at the top of the action (alongside
            // the configuration load); reuse it here for the entry fetch.
            EntryViewState entry = null;
            if (!tokenData.anonymousUser)
            {
                // Fetch the user's entry against the resolved per-service id so
                // the hero can surface "You're watching · Ep 5/12 · Your score:
                // 8.0" alongside the public meta. Best-effort: failures swallow
                // and the page renders without the user-state panel.
                try
                {
                    var resolvedEntryId = await _mappingService.GetIdByService(anime.id, animeService);
                    var entryId = string.IsNullOrEmpty(resolvedEntryId) ? anime.id : (animeService switch
                    {
                        AnimeService.Anilist     => $"{anilistPrefix}{resolvedEntryId}",
                        AnimeService.MyAnimeList => $"{malPrefix}{resolvedEntryId}",
                        _                        => $"{kitsuPrefix}{resolvedEntryId}",
                    });

                    var raw = animeService switch
                    {
                        AnimeService.Anilist     => await _anilistService.GetAnimeEntryAsync(tokenData, entryId, null),
                        AnimeService.MyAnimeList => await _malService.GetAnimeEntryAsync(tokenData, entryId, null),
                        _                        => await _kitsuService.GetAnimeEntryAsync(tokenData, entryId, null),
                    };

                    if (raw != null && !string.IsNullOrEmpty(raw.Status))
                    {
                        entry = new EntryViewState
                        {
                            Status = raw.Status,
                            Progress = raw.Progress,
                            TotalEpisodes = raw.TotalEpisodes,
                            UserScore = raw.Score,
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AnimeController.Detail: entry fetch failed for {Id}.", anime.id);
                }
            }

            // Cross-service links surfaced in the hero ("Open on AniList / MAL /
            // Kitsu / IMDb"). The mapping lookup is keyed on the anime's own id —
            // whichever service the page was loaded against, the same row of the
            // mapping dataset carries the sibling-service ids and the imdb tt id
            // when known. Best-effort: missing mapping = no links rendered, which
            // is the correct degradation for entries outside the curated dataset.
            var sourceLinks = await BuildSourceLinksAsync(anime.id);

            // Related + recommendations + supplementary chip rows (Tag / Studio /
            // director / writer / Composer / Artist / Producer / Staff) are now
            // fetched client-side after page render via /anime/{id}/extras — see
            // the Extras action below. Keeps the hero + episodes painting on the
            // GetAnimeByIdAsync result alone instead of waiting for the extra
            // AniList round-trip.
            //
            // DeferredSupplementaryLinks is only true for non-AniList primaries
            // because the AniList per-anime GraphQL already returns the chip
            // data inline in anime.links — paying for another /extras call there
            // would be redundant. The placeholder for those chips only renders
            // on non-AniList pages with a resolvable anilist id.
            var deferredSupplementaryLinks = sourceLinks.AnilistId.HasValue
                && !string.IsNullOrEmpty(anime.id)
                && !anime.id.StartsWith(anilistPrefix);

            return View(new AnimeDetailViewModel
            {
                Anime = anime,
                AnimeService = animeService,
                AnonymousUser = tokenData.anonymousUser,
                ConfigUid = uid,
                Entry = entry,
                SourceLinks = sourceLinks,
                DeferredSupplementaryLinks = deferredSupplementaryLinks,
            });
        }

        private async Task<List<Meta>> TryGetRelatedAsync(int anilistId, AnimeService translateTo)
        {
            try { return await _anilistFallback.GetRelatedAsync(anilistId, translateTo); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeController.Detail: related fetch failed for anilist {Id}.", anilistId);
                return [];
            }
        }

        // Companion JSON endpoint to Detail — returns the three below-the-fold
        // sections (related, recommendations, supplementary chip rows) so the
        // detail view can render its hero + episodes on the GetAnimeByIdAsync
        // result alone and hydrate the rest client-side after page load.
        // The three lists share one underlying GraphQL call (FetchSidedataAsync
        // inside AnilistFallback caches recommendations + relations + tag /
        // staff / studio in a single round-trip), so fanning these out into
        // separate client endpoints wouldn't actually parallelise upstream
        // work — one combined response is the right shape.
        // Route shape note: a catch-all parameter must be the last segment, so
        // we use /anime/extras/{*id} rather than /anime/{*id}/extras (the
        // latter is invalid in ASP.NET Core routing). The placeholder script
        // in Detail.cshtml builds the URL accordingly.
        [Route("/anime/extras/{*id}")]
        public async Task<IActionResult> Extras(string id)
        {
            if (string.IsNullOrEmpty(id)) return Json(new AnimeExtrasResponse());

            var tokenData = await _tokenService.GetAccessTokenAsync()
                ?? new TokenData { anime_service = AnimeService.Kitsu };
            var animeService = tokenData.anime_service;

            // Cross-service id resolution mirrors what Detail does so the
            // same /anime/{id}/extras URL works regardless of which service-
            // prefix the page was loaded against.
            var resolvedId = await ResolveToServiceIdAsync(id, animeService) ?? id;
            var sourceLinks = await BuildSourceLinksAsync(resolvedId);
            if (!sourceLinks.AnilistId.HasValue) return Json(new AnimeExtrasResponse());

            var anilistId = sourceLinks.AnilistId.Value;
            // All three lookups hit the same cached sidedata bundle inside
            // AnilistFallback — kicking them off in parallel lets the first
            // call populate the cache while the other two await on the
            // resulting Task rather than each firing a redundant upstream
            // request. Try/catch each so a partial failure still returns the
            // pieces that succeeded.
            var relatedTask = TryGetRelatedAsync(anilistId, animeService);
            var recommendationsTask = TryGetRecommendationsAsync(anilistId, animeService);
            var supplementaryTask = TryGetSupplementaryLinksAsync(anilistId);
            await Task.WhenAll(relatedTask, recommendationsTask, supplementaryTask);

            return Json(new AnimeExtrasResponse
            {
                Related = relatedTask.Result,
                Recommendations = recommendationsTask.Result,
                SupplementaryLinks = supplementaryTask.Result,
            });
        }

        private async Task<List<Meta>> TryGetRecommendationsAsync(int anilistId, AnimeService translateTo)
        {
            try { return await _anilistFallback.GetRecommendationMetasAsync(anilistId, translateTo); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeController.Extras: recommendations fetch failed for anilist {Id}.", anilistId);
                return [];
            }
        }

        private async Task<List<Link>> TryGetSupplementaryLinksAsync(int anilistId)
        {
            try { return await _anilistFallback.GetSupplementaryLinksAsync(anilistId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeController.Extras: supplementary-links fetch failed for anilist {Id}.", anilistId);
                return [];
            }
        }

        private async Task<AnimeSourceLinks> BuildSourceLinksAsync(string animeId)
        {
            var links = new AnimeSourceLinks();
            if (string.IsNullOrEmpty(animeId)) return links;

            try
            {
                // 1. Seed from the self id when it's in a service-native id
                //    space. The detail page now always renders ungrouped so
                //    anime.id arrives in the native space; the IMDb / TMDB
                //    branches stay for direct deep-links (e.g. when Stremio's
                //    grouped-cour card hands the user to /anime/tt5311514).
                AnimeIdMapping mapping = null;
                if (animeId.StartsWith(anilistPrefix)
                    && int.TryParse(animeId[anilistPrefix.Length..], out var aId))
                {
                    links.AnilistId = aId;
                    mapping = await _mappingService.GetAnilistMapping(animeId);
                }
                else if (animeId.StartsWith(malPrefix)
                    && int.TryParse(animeId[malPrefix.Length..], out var mId))
                {
                    links.MalId = mId;
                    mapping = await _mappingService.GetMalMapping(animeId);
                }
                else if (animeId.StartsWith(kitsuPrefix)
                    && int.TryParse(animeId[kitsuPrefix.Length..], out var kId))
                {
                    links.KitsuId = kId;
                    mapping = await _mappingService.GetKitsuMapping(animeId);
                }
                else if (animeId.StartsWith(imdbPrefix))
                {
                    // imdbPrefix is "tt" — the same shape IMDb itself uses.
                    // GetImdbMapping returns one entry per cour, ordered by
                    // season; take the first since that's what ResolveGroupedId
                    // collapses every cour to anyway. ImdbId is set
                    // unconditionally below from this id so the IMDb chip
                    // always renders on the grouped-cours code path.
                    links.ImdbId = animeId;
                    var imdbMappings = await _mappingService.GetImdbMapping(animeId);
                    mapping = imdbMappings.FirstOrDefault();
                }
                else if (animeId.StartsWith(tmdbPrefix))
                {
                    var tmdbMappings = await _mappingService.GetTmdbMapping(animeId);
                    mapping = tmdbMappings.FirstOrDefault();
                }

                // 2. Enrich with sibling ids from the cross-service mapping.
                //    ??= means a sibling id from the mapping fills in only
                //    when we don't already have one — so the prefix-derived
                //    self id (when present) wins over the mapping's
                //    sometimes-stale duplicate, but the IMDb-grouped code
                //    path still gets all four chips from the mapping alone.
                if (mapping != null)
                {
                    links.AnilistId ??= mapping.AnilistId;
                    links.MalId ??= mapping.MalId;
                    links.KitsuId ??= mapping.KitsuId;
                    if (string.IsNullOrEmpty(links.ImdbId)
                        && !string.IsNullOrEmpty(mapping.ImdbId)
                        && mapping.ImdbId.StartsWith("tt"))
                        links.ImdbId = mapping.ImdbId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BuildSourceLinks failed (id={Id}).", animeId);
            }

            return links;
        }

        // For imdb: ids, look up the cross-service mapping and translate to a
        // service-native id the per-service GetAnimeByIdAsync can handle. For
        // mal: ids consumed by non-MAL primaries, the same translation. Other
        // ids pass through unchanged.
        private async Task<string> ResolveToServiceIdAsync(string id, AnimeService service)
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
                var resolved = await _mappingService.GetIdByService(id, service);
                if (string.IsNullOrEmpty(resolved)) return null;
                return service switch
                {
                    AnimeService.Anilist => $"{anilistPrefix}{resolved}",
                    AnimeService.Kitsu   => $"{kitsuPrefix}{resolved}",
                    _                    => id,
                };
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

        // Mutate the meta's videos[] in place, prefixing each title with a
        // coloured emoji that signals the AnimeFillerList classification for
        // that episode. Mirrors MetaController.EnrichMetaWithFillerAsync. Best-
        // effort: any lookup failure / unknown show is silently a no-op so the
        // page still renders without prefixes.
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
                _logger.LogWarning(ex, "AnimeController: filler enrichment failed for {Name}.", meta?.name);
            }
        }
    }

    /// <summary>
    /// View model for the /anime/{id} detail page. Carries the resolved Meta
    /// (or null for the not-found render) plus the session-derived bits the
    /// view needs to decide whether to render the Edit button + user-state.
    /// </summary>
    public class AnimeDetailViewModel
    {
        public Meta Anime { get; set; }
        public AnimeService AnimeService { get; set; }
        public bool AnonymousUser { get; set; }
        public string ConfigUid { get; set; }
        // User's tracking state for this entry — null for anonymous visitors,
        // not-yet-tracked entries, or transient fetch failures (the hero
        // gracefully omits the user-state panel when this is null).
        public EntryViewState Entry { get; set; }
        // Cross-service links surfaced in the hero so users can jump to the
        // anime's page on AniList / MAL / Kitsu / IMDb. Resolved from the
        // shared AnimeIdMapping dataset — entries missing from the mapping
        // (e.g. obscure shows, donghua) simply omit the corresponding link.
        public AnimeSourceLinks SourceLinks { get; set; } = new();

        // True when the page should fire a client-side fetch of /anime/{id}/extras
        // to populate the supplementary chip rows (Tag/Studio/director/staff/etc.).
        // The data lives behind AniList's GraphQL; only fetch when we have an
        // anilist id to query against AND the page wasn't loaded against an AniList
        // primary (those entries already have the chip data inline from the main
        // meta call). Related + recommendations are always deferred regardless
        // of this flag.
        public bool DeferredSupplementaryLinks { get; set; }
    }

    /// <summary>
    /// Resolved external-site identifiers for the anime currently being viewed.
    /// All fields nullable; null means "no mapping found, don't render that link".
    /// </summary>
    public class AnimeSourceLinks
    {
        public int? AnilistId { get; set; }
        public int? MalId { get; set; }
        public int? KitsuId { get; set; }
        public string ImdbId { get; set; }
    }

    /// <summary>
    /// JSON payload for the /anime/{id}/extras endpoint — the three lists the
    /// detail view hydrates after page load. All three share one underlying
    /// AnilistFallback.FetchSidedataAsync GraphQL call, so the controller can
    /// kick them off in parallel without paying for separate upstream round-
    /// trips. Empty lists when the entry has no mapped AniList id (which is
    /// the same condition that gates the placeholder section emission).
    /// </summary>
    public class AnimeExtrasResponse
    {
        public List<Meta> Related { get; set; } = [];
        public List<Meta> Recommendations { get; set; } = [];
        public List<Link> SupplementaryLinks { get; set; } = [];
    }

    /// <summary>
    /// User-side tracking state surfaced on the detail page hero. A small
    /// projection of <see cref="ManageEntryViewModel"/> with just the four
    /// fields the page renders, so we don't carry the full edit-form payload
    /// where it isn't needed.
    /// </summary>
    public class EntryViewState
    {
        public string Status { get; set; }
        public int Progress { get; set; }
        public int? TotalEpisodes { get; set; }
        public double? UserScore { get; set; }
    }
}
