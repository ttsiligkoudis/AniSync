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
                return View("Detail", new AnimeDetailViewModel { Anime = null });
            }

            if (anime == null) return NotFound();

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

            // Prequel + sequel carousel. Driven off the anime's AniList id since
            // AniList's GraphQL is the only upstream that exposes explicit
            // PREQUEL / SEQUEL relations. For pages loaded against a non-anilist
            // id (kitsu: / mal: / tt…), the AnilistId surfaced by sourceLinks
            // already came from the cross-service mapping, so we reuse it — no
            // extra mapping round-trip needed.
            var related = sourceLinks.AnilistId.HasValue
                ? await TryGetRelatedAsync(sourceLinks.AnilistId.Value)
                : [];

            // Augment anime.links with AniList-sourced supplementary metadata
            // (Tag / Studio / director / writer / Composer / Artist / Producer /
            // Staff) when the page was loaded via a non-AniList service. The
            // KitsuService and MalService GetAnimeByIdAsync paths don't surface
            // this richness, so kitsu: / mal: pages would otherwise show no
            // Tag / Staff / director sections. anime.id (post-fetch) reflects
            // which service actually built the meta — including the Kitsu →
            // AniList fallback path inside the dispatch above — so the
            // !anilistPrefix check correctly skips the augment when AniList
            // already populated everything.
            if (sourceLinks.AnilistId.HasValue
                && !string.IsNullOrEmpty(anime.id)
                && !anime.id.StartsWith(anilistPrefix))
            {
                await AugmentLinksFromAnilistAsync(anime, sourceLinks.AnilistId.Value);
            }

            return View(new AnimeDetailViewModel
            {
                Anime = anime,
                AnimeService = animeService,
                AnonymousUser = tokenData.anonymousUser,
                ConfigUid = uid,
                Entry = entry,
                SourceLinks = sourceLinks,
                Related = related,
            });
        }

        private async Task<List<Meta>> TryGetRelatedAsync(int anilistId)
        {
            try { return await _anilistFallback.GetRelatedAsync(anilistId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeController.Detail: related fetch failed for anilist {Id}.", anilistId);
                return [];
            }
        }

        private async Task AugmentLinksFromAnilistAsync(Meta anime, int anilistId)
        {
            try
            {
                var supplementary = await _anilistFallback.GetSupplementaryLinksAsync(anilistId);
                if (supplementary == null || supplementary.Count == 0) return;

                anime.links ??= [];

                // Dedupe by (category, name) so a Studio the service already
                // surfaced (Kitsu has some studio links) doesn't render twice
                // alongside the AniList copy. Case-insensitive name compare
                // since "Mappa" / "MAPPA" are the same studio.
                var existing = new HashSet<(string, string)>(
                    anime.links
                        .Where(l => !string.IsNullOrEmpty(l.name))
                        .Select(l => (l.category ?? string.Empty, l.name.ToLowerInvariant())));

                foreach (var link in supplementary)
                {
                    if (string.IsNullOrEmpty(link.name)) continue;
                    var key = (link.category ?? string.Empty, link.name.ToLowerInvariant());
                    if (existing.Add(key))
                        anime.links.Add(link);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeController.Detail: link augment failed for anilist {Id}.", anilistId);
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

        // Prequels + sequels for this anime, ordered chronologically by air
        // year. Driven off AniList's explicit PREQUEL/SEQUEL relations
        // (not the IMDb-mapping "same cours" grouping, which is a different
        // concept). Empty for anime AniList doesn't index or that simply
        // have no prequel/sequel entries.
        public List<Meta> Related { get; set; } = [];
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
