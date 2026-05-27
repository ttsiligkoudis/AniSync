using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnimeList.Controllers
{
    // Stremio addon protocol route — open CORS (Stremio fetches it cross-origin)
    // and a generous per-UID rate limit (see the "addon" policy in Program.cs).
    [ApiController]
    [EnableCors("AddonCors")]
    [EnableRateLimiting("addon")]
    public class CatalogController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly IConfigStore _configStore;
        private readonly ILogger<CatalogController> _logger;

        public CatalogController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService, IMalService malService, IConfigStore configStore, ILogger<CatalogController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
            _logger = logger;
        }

        [HttpGet("{config}/[controller]/{metaType}/{listType}/{extras}.json")]
        public async Task<ActionResult> GetListWithExtras(string config, MetaType metaType, ListType listType, string extras)
        {
            var parsed = ParseExtras(extras);
            parsed.TryGetValue("skip", out var skip);
            parsed.TryGetValue("genre", out var genre);
            parsed.TryGetValue("search", out var search);
            parsed.TryGetValue("sort", out var sort);
            return await GetList(config, metaType, listType, skip, genre: genre, search: search, sort: sort);
        }

        [HttpGet("{config}/[controller]/{metaType}/{listType}.json")]
        public async Task<ActionResult> GetList(string config, MetaType metaType, ListType listType, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null)
        {
            try
            {
                if (genre?.Equals(DefaultOption, StringComparison.OrdinalIgnoreCase) == true) genre = null;

                var tokenData = await _tokenService.GetAccessTokenAsync(config);
                var animeService = tokenData?.anime_service ?? AnimeService.Kitsu;

                if (!string.IsNullOrWhiteSpace(tokenData?.access_token) && IsTokenExpired(tokenData.expiration_date))
                {
                    return new JsonResult(new { metas = ExpiredMetas() });
                }

                // The "Group anime seasons" toggle defaults OFF (enableSeasonGrouping=false);
                // when the user opts in, services collapse a franchise's cours via the IMDb/
                // TMDB cross-service mapping instead of emitting each cour's native id.
                var configuration = await ResolveConfigAsync(config, _configStore);
                var groupSeasons = configuration?.enableSeasonGrouping == true;
                var hideUnreleased = configuration?.hideUnreleasedFromWatching == true;
                // 18+ gate — anonymous (no config) and default-zero installs
                // get family-safe catalogs; users opt in via /configure.
                var hideAdult = configuration?.showAdultContent != true;

                // Search always runs with groupSeasons=false so the per-service dedup
                // doesn't rewrite a movie's name to the shortest among entries that
                // share an IMDb id. Mirrors /api/v1/match exactly — same upstream
                // call, same scoring, same ordering. The user's groupSeasons toggle
                // still applies to every other catalog (user lists / Trending /
                // Seasonal / Airing).
                var groupSeasonsForCall = listType == ListType.Search ? false : groupSeasons;

                // When AniList is the user's primary and the upstream is currently
                // down, fall back to Kitsu for non-user-list catalogs so Stremio
                // rows don't go empty. User lists (Currently Watching / Completed
                // / etc.) live in the user's AniList account — there's nothing to
                // fall back to, so we let those return empty during an outage.
                var isUserList = listType is ListType.Current or ListType.Completed
                    or ListType.Planning or ListType.Paused
                    or ListType.Dropped or ListType.Repeating;
                var fallbackToKitsu = animeService == AnimeService.Anilist
                    && AnimeList.Services.AnilistHealthMonitor.IsDown
                    && !isUserList;
                var catalogService = fallbackToKitsu ? AnimeService.Kitsu : animeService;
                var catalogToken = fallbackToKitsu
                    ? new TokenData { anime_service = AnimeService.Kitsu }
                    : tokenData;

                var metas = catalogService switch
                {
                    AnimeService.Anilist => await _anilistService.GetAnimeListAsync(catalogToken, listType, skip, animeId, genre, search, sort, groupSeasonsForCall, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                    AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(catalogToken, listType, skip, animeId, genre, search, sort, groupSeasonsForCall, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                    _ => await _kitsuService.GetAnimeListAsync(catalogToken, listType, skip, animeId, genre, search, sort, groupSeasonsForCall, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                };

                // Search splits into separate series + movie catalogs in the manifest
                // so Stremio renders two result rows. The upstream search hit returns
                // both — filter by the route's metaType so each row carries only the
                // matching shape. Case-insensitive equality defends against a
                // service builder that ever sets type to "Series" / "Movie" instead
                // of the lowercase canonical form.
                var preFilterCount = metas?.Count ?? 0;
                if (listType == ListType.Search && (metaType == MetaType.series || metaType == MetaType.movie))
                {
                    var typeName = metaType.ToString();
                    metas = metas
                        .Where(m => string.Equals(m.type, typeName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    _logger.LogInformation(
                        "Catalog search filter: type={MetaType} kept {Kept} of {Total} metas (service={Service}, search={Search}).",
                        typeName, metas.Count, preFilterCount, animeService, search);
                }

                // Re-rank Search results by the shared Utils.ScoreMatch — same
                // helper /api/v1/match uses. A relevance threshold trims the long
                // tail of low-overlap matches that the user finds noisy in the
                // search row; /match keeps everything because its callers want a
                // ranked list, while Stremio's row is small (~10 visible cards) so
                // a strict cutoff reads better.
                if (listType == ListType.Search && !string.IsNullOrWhiteSpace(search) && metas.Count > 0)
                {
                    var normalisedQuery = NormalizeTitle(search);
                    const double minScore = 0.4;
                    metas = metas
                        .Select(m => (meta: m, score: ScoreMatch(normalisedQuery, m.name)))
                        .Where(x => x.score >= minScore)
                        .OrderByDescending(x => x.score)
                        .Select(x => x.meta)
                        .ToList();
                }

                return new JsonResult(new { metas });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Catalog request failed (listType={ListType}, animeId={AnimeId}, genre={Genre}, search={Search}, sort={Sort}, skip={Skip}).",
                    listType, animeId, genre, search, sort, skip);
                // Stremio expects a JSON envelope even on failure — empty metas keeps the
                // catalog row from rendering "(unknown error)" in the UI.
                return new JsonResult(new { metas = Array.Empty<object>() });
            }
        }

        /// <summary>
        /// Parses Stremio extras path segment (e.g. "genre=Action&amp;skip=50") into key-value pairs.
        /// </summary>
        private static Dictionary<string, string> ParseExtras(string extras)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(extras)) return result;

            foreach (var part in extras.Split('&'))
            {
                var eqIndex = part.IndexOf('=');
                if (eqIndex > 0)
                {
                    var key = Uri.UnescapeDataString(part[..eqIndex]);
                    var value = Uri.UnescapeDataString(part[(eqIndex + 1)..]);
                    result[key] = value;
                }
            }

            return result;
        }
    }
}

