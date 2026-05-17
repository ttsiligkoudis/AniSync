using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class CatalogController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly IConfigStore _configStore;
        private readonly IUserListCache _listCache;
        private readonly ILogger<CatalogController> _logger;

        // Stremio re-requests the same catalog often (every Home / Discover open,
        // every paginated scroll) — the six user-list types defined here mirror
        // UserListCache.CachedListTypes so the read path through the cache stays
        // consistent with what gets invalidated on writes.
        private static readonly HashSet<ListType> UserListTypes =
        [
            ListType.Current,
            ListType.Completed,
            ListType.Planning,
            ListType.Paused,
            ListType.Dropped,
            ListType.Repeating,
        ];

        public CatalogController(ITokenService tokenService, IAnilistService anilistService, IKitsuService kitsuService, IMalService malService, IConfigStore configStore, IUserListCache listCache, ILogger<CatalogController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _configStore = configStore;
            _listCache = listCache;
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

                // Search always runs with groupSeasons=false so the per-service dedup
                // doesn't rewrite a movie's name to the shortest among entries that
                // share an IMDb id. Mirrors /api/v1/match exactly — same upstream
                // call, same scoring, same ordering. The user's groupSeasons toggle
                // still applies to every other catalog (user lists / Trending /
                // Seasonal / Airing).
                var groupSeasonsForCall = listType == ListType.Search ? false : groupSeasons;

                // Route the six user-list types through UserListCache so a grouping-enabled
                // user only hits AniList/MAL/Kitsu once per 10 minutes regardless of how
                // often Stremio re-asks for the same catalog. The cache decides internally
                // whether the call actually short-circuits (only for groupSeasons=true on
                // an authenticated user) — every other branch falls through to the fetcher.
                Task<List<Meta>> Fetch() => animeService switch
                {
                    AnimeService.Anilist => _anilistService.GetAnimeListAsync(tokenData, listType, skip, animeId, genre, search, sort, groupSeasonsForCall, hideUnreleased: hideUnreleased),
                    AnimeService.MyAnimeList => _malService.GetAnimeListAsync(tokenData, listType, skip, animeId, genre, search, sort, groupSeasonsForCall, hideUnreleased: hideUnreleased),
                    _ => _kitsuService.GetAnimeListAsync(tokenData, listType, skip, animeId, genre, search, sort, groupSeasonsForCall, hideUnreleased: hideUnreleased),
                };

                var metas = UserListTypes.Contains(listType)
                    ? await _listCache.GetOrFetchAsync(tokenData, listType, groupSeasonsForCall, Fetch)
                    : await Fetch();

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

