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
                    anime = await _kitsuService.GetAnimeByIdAsync(id, tokenData, groupSeasons: true);
                    if (anime == null)
                    {
                        var mapping = await _mappingService.GetKitsuMapping(id);
                        if (mapping?.AnilistId != null)
                            anime = await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons: true);
                    }
                }
                else if (id.StartsWith(anilistPrefix))
                {
                    anime = await _anilistService.GetAnimeByIdAsync(id, tokenData, groupSeasons: true);
                    if (anime == null)
                    {
                        var mapping = await _mappingService.GetAnilistMapping(id);
                        if (mapping?.KitsuId != null)
                            anime = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons: true);
                    }
                }
                else if (id.StartsWith(malPrefix))
                {
                    anime = await _malService.GetAnimeByIdAsync(id, tokenData, groupSeasons: true);
                    if (anime == null)
                    {
                        var mapping = await _mappingService.GetMalMapping(id);
                        if (mapping?.AnilistId != null)
                            anime = await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons: true);
                        else if (mapping?.KitsuId != null)
                            anime = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons: true);
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

            // Resolve UID for logged-in users so the Edit button's data-meta-id
            // hooks the existing modal flow.
            string uid = null;
            EntryViewState entry = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;

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

            return View(new AnimeDetailViewModel
            {
                Anime = anime,
                AnimeService = animeService,
                AnonymousUser = tokenData.anonymousUser,
                ConfigUid = uid,
                Entry = entry,
            });
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
