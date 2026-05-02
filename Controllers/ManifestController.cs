using AnimeList.Models;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class ManifestController : ControllerBase
    {
        [HttpGet("{config}/manifest.json")]
        public JsonResult Get(string config)
        {
            var configuration = DecodeConfig(config);
            var isAuthenticated = !string.IsNullOrWhiteSpace(configuration?.tokenData);
            TokenData? tokenData = null;
            var name = "AniSync";

#if DEBUG
            name = "AniSyncDev";
#endif

            var manifest = new Manifest
            {
                id = "community.AniSync",
                version = "1.0.0",
                name = name,
                description = "Fetches anime list from Kitsu/AniList to track your anime progress while using stremio",
                logo = $"{Request.Scheme}://{Request.Host}/logo.png",
                resources = [ "catalog", "meta", "subtitles", "stream" ],
                types = [ MetaType.movie.ToString(), MetaType.series.ToString() ],
                behaviorHints = new BehaviorHints
                {
                    configurable = true,
                },
                idPrefixes = [anilistPrefix, kitsuPrefix, imdbPrefix, tmdbPrefix]
            };

            if (isAuthenticated)
            {
                tokenData = DeserializeObject<TokenData>(configuration.tokenData);

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
                    configuration.discoverOnlyCurrent, currentListVariant: true));

            if (isAuthenticated && configuration.showCompleted)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Completed, "Completed", tokenData,
                    configuration.discoverOnlyCompleted));

            if (isAuthenticated && configuration.showPlanning)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Planning, "Plan to Watch", tokenData,
                    configuration.discoverOnlyPlanning));

            if (isAuthenticated && configuration.showPaused)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Paused, "On Hold", tokenData,
                    configuration.discoverOnlyPaused));

            if (isAuthenticated && configuration.showDropped)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Dropped, "Dropped", tokenData,
                    configuration.discoverOnlyDropped));

            // AniList-only: Kitsu has no "repeating" status
            if (isAuthenticated && configuration.showRepeating
                && tokenData?.anime_service == AnimeService.Anilist)
                manifest.catalogs.Add(BuildUserListCatalog(ListType.Repeating, "Rewatching", tokenData,
                    configuration.discoverOnlyRepeating));

            if (configuration.showTrending)
            {
                manifest.catalogs.Add(new Catalog
                {
                    type = MetaType.anime.ToString(),
                    id = ListType.Trending_Desc.ToString(),
                    name = "Trending Now",
                    extra = [new("skip"), new("genre") { options = GetOptions(configuration.discoverOnlyTrending), isRequired = configuration.discoverOnlyTrending }],
                });
            }

            if (configuration.showSeasonal)
            {
                manifest.catalogs.Add(new Catalog
                {
                    type = MetaType.anime.ToString(),
                    id = ListType.Seasonal.ToString(),
                    name = "Seasonal Anime",
                    extra = [new("skip"), new("genre") { options = SeasonOptions, isRequired = configuration.discoverOnlySeasonal }],
                });
            }

            return new JsonResult(manifest);
        }

        private static Catalog BuildUserListCatalog(ListType list, string name, TokenData tokenData,
            bool discoverOnly, bool currentListVariant = false)
        {
            // Currently-watching has no genre options unless the user explicitly set discover-only;
            // every other user list always shows the full genre list.
            var genreOptions = currentListVariant
                ? (discoverOnly ? [DefaultOption] : new List<string>())
                : GetOptions(discoverOnly);

            return new Catalog
            {
                type = MetaType.anime.ToString(),
                id = list.ToString(),
                name = name,
                extra = [new("skip"), new("genre") { options = genreOptions, isRequired = discoverOnly }],
            };
        }
    }
}
