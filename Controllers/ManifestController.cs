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
            var configiration = DeserializeObject<Configuration>(config);
            var isAuthenticated = !string.IsNullOrWhiteSpace(configiration?.tokenData);
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
                resources = [ "catalog", "meta", "subtitles" ],
                types = [ MetaType.movie.ToString(), MetaType.series.ToString(), MetaType.anime.ToString() ],
                behaviorHints = new BehaviorHints
                {
                    configurable = true,
                },
            };

            if (isAuthenticated)
            {
                tokenData = DeserializeObject<TokenData>(DecompressString(Uri.UnescapeDataString(configiration.tokenData)));

                manifest.config.Add(new Config
                {
                    key = "token",
                    type = "text",
                    title = "token"
                });

                if (tokenData.anime_service == AnimeService.Kitsu)
                    manifest.idPrefixes.Add(kitsuPrefix);
                else
                {
                    manifest.idPrefixes.Add(anilistPrefix);
                    manifest.idPrefixes.Add(kitsuPrefix);
                }

                manifest.idPrefixes.Add(imdbPrefix);
                manifest.idPrefixes.Add(tmdbPrefix);
            }
            else
            {
                manifest.idPrefixes.Add(kitsuPrefix);
                manifest.idPrefixes.Add(imdbPrefix);
                manifest.idPrefixes.Add(tmdbPrefix);
            }

            if (isAuthenticated && configiration.showCurrent)
            {
                manifest.catalogs.Add(new Catalog
                {
                    type = MetaType.anime.ToString(),
                    id = GetListTypeString(ListType.Current, tokenData),
                    name = "Currently watching",
                    extra = [new("skip"), new("genre") { options = configiration.discoverOnlyCurrent ? [DefaultOption] : [], isRequired = configiration.discoverOnlyCurrent }],
                });
            }

            if (isAuthenticated && configiration.showCompleted)
            {
                manifest.catalogs.Add(new Catalog
                {
                    type = MetaType.anime.ToString(),
                    id = GetListTypeString(ListType.Completed, tokenData),
                    name = "Completed",
                    extra = [new("skip"), new("genre") { options = AnimeGenres, isRequired = configiration.discoverOnlyCompleted }],
                });
            }

            if (configiration.showTrending)
            {
                manifest.catalogs.Add(new Catalog
                {
                    type = MetaType.anime.ToString(),
                    id = GetListTypeString(ListType.Trending_Desc, tokenData),
                    name = "Trending Now",
                    extra = [new("skip"), new("genre") { options = AnimeGenres, isRequired = configiration.discoverOnlyTrending }],
                });
            }

            if (configiration.showSeasonal)
            {
                manifest.catalogs.Add(new Catalog
                {
                    type = MetaType.anime.ToString(),
                    id = GetListTypeString(ListType.Seasonal, tokenData),
                    name = "Seasonal Anime",
                    extra = [new("skip"), new("genre") { options = SeasonOptions, isRequired = configiration.discoverOnlySeasonal }],
                });
            }

            return new JsonResult(manifest);
        }
    }
}


