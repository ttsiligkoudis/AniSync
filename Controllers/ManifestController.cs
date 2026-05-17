using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class ManifestController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;
        private readonly ILogger<ManifestController> _logger;

        public ManifestController(ITokenService tokenService, IConfigStore configStore, ILogger<ManifestController> logger)
        {
            _tokenService = tokenService;
            _configStore = configStore;
            _logger = logger;
        }

        [HttpGet("{config}/manifest.json")]
        public async Task<JsonResult> Get(string config)
        {
            try
            {
                return await BuildManifestAsync(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manifest build failed (config={Config}).", config);
                // Stremio rejects an addon whose manifest 5xxs, so emit a minimal valid
                // shell — the search catalog alone is enough for the addon to register.
                // The user will see no catalogs until the underlying issue is fixed.
                return new JsonResult(new Manifest
                {
                    id = "community.AniSync",
                    version = "1.0.0",
                    name = "AniSync",
                    description = "AniSync (degraded mode — see server logs).",
                    resources = ["catalog", "meta", "subtitles", "stream"],
                    types = [MetaType.movie.ToString(), MetaType.series.ToString()],
                    behaviorHints = new BehaviorHints { configurable = true },
                    idPrefixes = [anilistPrefix, kitsuPrefix, imdbPrefix, tmdbPrefix, malPrefix],
                });
            }
        }

        private async Task<JsonResult> BuildManifestAsync(string config)
        {
            // Hydrates flags from the config store for v5 URLs; v3 (anonymous) carries
            // its flags inline so resolution is a no-op for that branch.
            var configuration = await ResolveConfigAsync(config, _configStore);

            // For v3 (inline tokenData) the in-URL JSON is enough to tell us "logged in";
            // for v5 we go through TokenService, which knows how to fetch from the store.
            TokenData tokenData = null;
            if (configuration != null)
            {
                tokenData = !string.IsNullOrEmpty(configuration.tokenData)
                    ? DeserializeObject<TokenData>(configuration.tokenData)
                    : await _tokenService.GetAccessTokenAsync(config);
            }
            var isAuthenticated = tokenData != null && !tokenData.anonymousUser;
            // Drives whether user-list catalogs render with the `skip` extra. When
            // grouping is off the underlying service can stream one upstream page
            // at a time (MAL/Kitsu — AniList's MediaListCollection has no nested
            // pagination, so it still fetches the whole list and gets cached only
            // when grouping is on).
            var groupSeasons = configuration?.enableSeasonGrouping == true;
            var animeService = tokenData?.anime_service ?? AnimeService.Kitsu;
            var name = "AniSync";

#if DEBUG
            name = "AniSyncDev";
#endif

            var manifest = new Manifest
            {
                id = "community.AniSync",
                version = "1.0.0",
                name = name,
                description = "Fetches anime list from Kitsu/AniList/MyAnimeList to track your anime progress while using stremio",
                logo = $"{Request.Scheme}://{Request.Host}/logo.png",
                resources = [ "catalog", "meta", "subtitles", "stream" ],
                types = [ MetaType.movie.ToString(), MetaType.series.ToString() ],
                behaviorHints = new BehaviorHints
                {
                    configurable = true,
                },
                idPrefixes = [anilistPrefix, kitsuPrefix, imdbPrefix, tmdbPrefix, malPrefix]
            };

            if (isAuthenticated)
            {
                manifest.config.Add(new Config
                {
                    key = "token",
                    type = "text",
                    title = "token"
                });
            }

            // User-list catalogs require authentication. The "Currently watching" catalog
            // intentionally has no genre options unless discover-only is set.
            if (isAuthenticated && configuration.showCurrent)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Current, "Currently watching", tokenData,
                    configuration.discoverOnlyCurrent, groupSeasons, animeService, currentListVariant: true));

            if (isAuthenticated && configuration.showCompleted)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Completed, "Completed", tokenData,
                    configuration.discoverOnlyCompleted, groupSeasons, animeService));

            if (isAuthenticated && configuration.showPlanning)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Planning, "Plan to Watch", tokenData,
                    configuration.discoverOnlyPlanning, groupSeasons, animeService));

            if (isAuthenticated && configuration.showPaused)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Paused, "On Hold", tokenData,
                    configuration.discoverOnlyPaused, groupSeasons, animeService));

            if (isAuthenticated && configuration.showDropped)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Dropped, "Dropped", tokenData,
                    configuration.discoverOnlyDropped, groupSeasons, animeService));

            // AniList and MyAnimeList both expose a rewatching concept (a separate status on
            // AniList, an is_rewatching boolean on MAL); Kitsu has neither.
            if (isAuthenticated && configuration.showRepeating
                && (animeService == AnimeService.Anilist
                    || animeService == AnimeService.MyAnimeList))
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Repeating, "Rewatching", tokenData,
                    configuration.discoverOnlyRepeating, groupSeasons, animeService));

            if (configuration?.showTrending == true)
            {
                manifest.catalogs.Add(new Catalog
                {
                    type = MetaType.anime.ToString(),
                    id = ListType.Trending_Desc.ToString(),
                    name = "Trending Now",
                    extra =
                    [
                        new("skip"),
                        new("genre") { options = GetOptions(configuration.discoverOnlyTrending), isRequired = configuration.discoverOnlyTrending },
                        new("sort") { options = SortOptions },
                    ],
                });
            }

            if (configuration?.showSeasonal == true)
            {
                manifest.catalogs.Add(new Catalog
                {
                    type = MetaType.anime.ToString(),
                    id = ListType.Seasonal.ToString(),
                    name = "Seasonal Anime",
                    extra =
                    [
                        new("skip"),
                        new("genre") { options = SeasonOptions, isRequired = configuration.discoverOnlySeasonal },
                        new("sort") { options = SortOptions },
                    ],
                });
            }

            if (configuration?.showAiring == true)
            {
                // Airing has no meaningful filter axis, so we use the same trick as the
                // "Currently watching" catalog: when discover-only is on, declare a required
                // "genre" extra with a single placeholder option. Stremio's Home/Dashboard
                // can't auto-fill required extras, so the catalog only renders in Discover.
                var genreOptions = configuration.discoverOnlyAiring ? [DefaultOption] : new List<string>();
                manifest.catalogs.Add(new Catalog
                {
                    type = MetaType.anime.ToString(),
                    id = ListType.Airing.ToString(),
                    name = "Airing This Week",
                    extra =
                    [
                        new("skip"),
                        new("genre") { options = genreOptions, isRequired = configuration.discoverOnlyAiring },
                    ],
                });
            }

            // Search catalogs: split into series + movie rows so Stremio renders two
            // result strips (Anime and Anime Movies) instead of a single mixed grid.
            // Same Search id and same upstream call — CatalogController filters by
            // type. Search extra is required so neither row appears on the discover
            // page; both fire only when the user types a query.
            manifest.catalogs.Add(new Catalog
            {
                type = MetaType.series.ToString(),
                id = ListType.Search.ToString(),
                name = "Anime",
                extra = [new("search") { isRequired = true }, new("skip")],
            });
            manifest.catalogs.Add(new Catalog
            {
                type = MetaType.movie.ToString(),
                id = ListType.Search.ToString(),
                // Same catalog name as the series row above so Stremio's "<name> - <type>"
                // label format renders as "Anime - Series" / "Anime - Movie" — symmetrical
                // prefix, type suffix supplied by Stremio.
                name = "Anime",
                extra = [new("search") { isRequired = true }, new("skip")],
            });

            return new JsonResult(manifest);
        }

        private static Catalog BuildUserListCatalog(ListType list, string name, TokenData tokenData,
            bool discoverOnly, bool groupSeasons, AnimeService service, bool currentListVariant = false)
        {
            // Currently-watching has no genre options unless the user explicitly set discover-only;
            // every other user list always shows the full genre list.
            var genreOptions = currentListVariant
                ? (discoverOnly ? [DefaultOption] : new List<string>())
                : GetOptions(discoverOnly);

            var extras = new List<Extra>
            {
                new("genre") { options = genreOptions, isRequired = discoverOnly },
            };
            // Expose `skip` so Stremio paginates when grouping is off and the upstream
            // can stream one page per request. Skipped for AniList because its
            // MediaListCollection GraphQL has no nested pagination — the server would
            // still fetch everything internally, so paging would just be cosmetic. With
            // grouping on the result is cached in full, also no paging needed.
            if (!groupSeasons && service != AnimeService.Anilist)
            {
                extras.Add(new Extra("skip"));
            }

            return new Catalog
            {
                type = MetaType.anime.ToString(),
                id = list.ToString(),
                name = name,
                extra = extras,
            };
        }
    }
}
