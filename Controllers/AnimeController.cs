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
        private readonly ILogger<AnimeController> _logger;

        public AnimeController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            ITmdbService tmdbService,
            IAnimeMappingService mappingService,
            IConfigStore configStore,
            ILogger<AnimeController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _tmdbService = tmdbService;
            _mappingService = mappingService;
            _configStore = configStore;
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

            // Resolve UID for logged-in users so the Edit button's data-meta-id
            // hooks the existing modal flow.
            string uid = null;
            if (!tokenData.anonymousUser)
            {
                var (resolved, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                uid = resolved;
            }

            return View(new AnimeDetailViewModel
            {
                Anime = anime,
                AnimeService = animeService,
                AnonymousUser = tokenData.anonymousUser,
                ConfigUid = uid,
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
    }

    /// <summary>
    /// View model for the /anime/{id} detail page. Carries the resolved Meta
    /// (or null for the not-found render) plus the session-derived bits the
    /// view needs to decide whether to render the Edit button.
    /// </summary>
    public class AnimeDetailViewModel
    {
        public Meta Anime { get; set; }
        public AnimeService AnimeService { get; set; }
        public bool AnonymousUser { get; set; }
        public string ConfigUid { get; set; }
    }
}
