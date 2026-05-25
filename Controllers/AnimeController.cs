using AnimeList.Models;
using AnimeList.Services;
using AnimeList.Services.Extensions;
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
        private readonly IAnilistFallback _anilistFallback;
        private readonly IAnimeMetaLoader _animeMetaLoader;
        private readonly IAddonStreamService _addonStreamService;
        private readonly IAniSkipService _aniSkipService;
        private readonly ISubtitleService _subtitleService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ISyncService _syncService;
        private readonly ILogger<AnimeController> _logger;

        public AnimeController(
            ITokenService tokenService,
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            ITmdbService tmdbService,
            IAnimeMappingService mappingService,
            IConfigStore configStore,
            IAnilistFallback anilistFallback,
            IAnimeMetaLoader animeMetaLoader,
            IAddonStreamService addonStreamService,
            IAniSkipService aniSkipService,
            ISubtitleService subtitleService,
            IHttpClientFactory httpClientFactory,
            ISyncService syncService,
            ILogger<AnimeController> logger)
        {
            _tokenService = tokenService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _tmdbService = tmdbService;
            _mappingService = mappingService;
            _configStore = configStore;
            _anilistFallback = anilistFallback;
            _animeMetaLoader = animeMetaLoader;
            _addonStreamService = addonStreamService;
            _aniSkipService = aniSkipService;
            _subtitleService = subtitleService;
            _httpClientFactory = httpClientFactory;
            _syncService = syncService;
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
            // to render the page. ResolveCurrentAsync returns null uid for
            // anonymous / unauthenticated, so that double-purpose handles the
            // existing "anonymousUser → no uid" branching for us.
            var (tokenData, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            tokenData ??= new TokenData { anime_service = AnimeService.Kitsu };
            var animeService = tokenData.anime_service;

            // 18+ gate gets its toggle from the user's config; null for
            // anonymous viewers reads as "no adult content" which matches
            // the per-service filtering on /discover / /library.
            var detailConfig = await GetConfigByUidAsync(uid, _configStore);
            var showAdultContent = detailConfig?.showAdultContent == true;

            // /anime/{id} is per-cour by default — the per-service detail
            // page wants the cour-specific data (videos, score, status) for
            // the entry the user clicked, not the franchise umbrella. The
            // grouped-imdb path inside the loader overrides this when the
            // URL itself already collapses the cours (id starts with tt)
            // so the page can render season tabs across every cour the
            // franchise has.
            var loadResult = await _animeMetaLoader.LoadAsync(id, tokenData, groupSeasons: false, showAdultContent: showAdultContent);

            if (loadResult.Anime == null)
            {
                // Mapping miss / upstream gone / adult-filtered. Hand off
                // to the shared 404 page so this matches what users see
                // on any other bad URL.
                Response.StatusCode = 404;
                return View("NotFound");
            }

            var anime = loadResult.Anime;
            var sourceLinks = loadResult.SourceLinks;
            var isMultiSeasonGroup = loadResult.IsMultiSeasonGroup;
            var imdbHeadSeason = loadResult.ImdbHeadSeason;
            var imdbHeadAnilistId = loadResult.ImdbHeadAnilistId;

            // uid was already resolved at the top of the action (alongside
            // the configuration load); reuse it here for the entry fetch.
            // Skipped on multi-cour grouped renders — the user's per-cour
            // entry can't represent the franchise as a whole, so the hero
            // shows a generic "Manage Entry" pill instead (driven by the
            // IsMultiSeasonGroup view-model flag below).
            EntryViewState entry = null;
            if (!tokenData.anonymousUser && !isMultiSeasonGroup)
            {
                // Fetch the user's entry against the resolved per-service id so
                // the hero can surface "You're watching · Ep 5/12 · Your score:
                // 8.0" alongside the public meta. Best-effort: failures swallow
                // and the page renders without the user-state panel.
                try
                {
                    var entryId = await _mappingService.GetIdWithPrefixAsync(anime.id, animeService) ?? anime.id;

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

            // Stream-source availability gate. Two independent signals: a
            // Real-Debrid API key on file (Torrentio → RD-cached torrents
            // surface on the watch page), or the External services toggle
            // turned on (Crunchyroll / Netflix / HiDive links from
            // GetExternalLinksAsync). Either alone is enough — they
            // populate different sections of the picker. Both off →
            // there's nothing to render on /watch, so the episode rows
            // stay inert here.
            bool hasAddons = false;
            bool externalEnabled = false;
            if (!string.IsNullOrEmpty(uid))
            {
                var watchConfig = await GetConfigByUidAsync(uid, _configStore);
                externalEnabled = watchConfig?.showExternalStreams == true;
                hasAddons = (await _configStore.GetStreamAddonsAsync(uid)).Count > 0;
            }

            return View(new AnimeDetailViewModel
            {
                Anime = anime,
                AnimeService = animeService,
                AnonymousUser = tokenData.anonymousUser,
                ConfigUid = uid,
                Entry = entry,
                SourceLinks = sourceLinks,
                DeferredSupplementaryLinks = deferredSupplementaryLinks,
                HasStreamSources = hasAddons || externalEnabled,
                IsMultiSeasonGroup = isMultiSeasonGroup,
            });
        }

        private async Task<List<Meta>> TryGetRelatedAsync(int anilistId, AnimeService translateTo, bool groupSeasons = false)
        {
            try { return await _anilistFallback.GetRelatedAsync(anilistId, translateTo, groupSeasons); }
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

            var (tokenData, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            tokenData ??= new TokenData { anime_service = AnimeService.Kitsu };
            var animeService = tokenData.anime_service;

            // Per-user grouping pref — recommendations + related lists hand
            // their card ids over to client-side render so the same id-space
            // rules Detail() applies (imdb-first when grouping is on) need to
            // flow through here too. Anonymous users / no-config installs
            // fall back to ungrouped (configuration?.enableSeasonGrouping is
            // null → false).
            var configuration = !string.IsNullOrEmpty(uid) ? await GetConfigByUidAsync(uid, _configStore) : null;
            var groupSeasons = configuration?.enableSeasonGrouping == true;

            // Cross-service id resolution mirrors what Detail does so the
            // same /anime/{id}/extras URL works regardless of which service-
            // prefix the page was loaded against.
            var resolvedId = await _animeMetaLoader.ResolveToServiceIdAsync(id, animeService) ?? id;
            var sourceLinks = await _mappingService.BuildSourceLinksAsync(resolvedId);
            if (!sourceLinks.AnilistId.HasValue) return Json(new AnimeExtrasResponse());

            var anilistId = sourceLinks.AnilistId.Value;
            // All three lookups hit the same cached sidedata bundle inside
            // AnilistFallback — kicking them off in parallel lets the first
            // call populate the cache while the other two await on the
            // resulting Task rather than each firing a redundant upstream
            // request. Try/catch each so a partial failure still returns the
            // pieces that succeeded.
            // Related is skipped when grouping is on — by definition a grouped
            // franchise umbrella card already includes the prequel/sequel
            // cours, so the Related rail would just duplicate the same
            // franchise links the user already navigated through.
            var relatedTask = groupSeasons
                ? Task.FromResult(new List<Meta>())
                : TryGetRelatedAsync(anilistId, animeService, groupSeasons);
            var recommendationsTask = TryGetRecommendationsAsync(anilistId, animeService, groupSeasons);
            var supplementaryTask = TryGetSupplementaryLinksAsync(anilistId);
            await Task.WhenAll(relatedTask, recommendationsTask, supplementaryTask);

            return Json(new AnimeExtrasResponse
            {
                Related = relatedTask.Result,
                Recommendations = recommendationsTask.Result,
                SupplementaryLinks = supplementaryTask.Result,
            });
        }

        /// <summary>
        /// Dedicated per-episode "watch" page. Replaces the in-page modal on
        /// /anime/{id}: episode rows on Detail.cshtml now navigate here so the
        /// user lands on a full screen with the Plyr-styled player, source
        /// picker, and prev / next episode buttons. The id segment carries
        /// the same colon-prefixed shapes /anime/{id} accepts
        /// (anilist:N / kitsu:N / mal:N / tt12345 / tmdb:N) — kept as a
        /// single segment because the existing /anime/{id} card links work
        /// the same way and Detail's catch-all is for absorbing slugs.
        /// </summary>
        [Route("/anime/{id}/watch/{episode:int}")]
        [Route("/anime/{id}/watch/{season:int}/{episode:int}")]
        public async Task<IActionResult> Watch(string id, int episode, int? season = null)
        {
            if (string.IsNullOrEmpty(id) || episode <= 0)
            {
                Response.StatusCode = 404;
                return View("NotFound");
            }

            var (tokenData, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            tokenData ??= new TokenData { anime_service = AnimeService.Kitsu };
            var animeService = tokenData.anime_service;

            // IMDb-id deep-link path mirrors Detail() — multi-cour franchise
            // umbrella with season-tagged videos. Direct anilist:/mal:/kitsu:
            // URLs stay per-cour (the user asked for that specific cour by
            // URL). Falls through to the per-service path on mapping miss.
            bool isImdbGrouped = id.StartsWith(imdbPrefix);
            const bool groupSeasons = false;
            Meta anime = null;
            int? imdbHeadSeason = null;
            int? imdbHeadAnilistId = null;
            bool renderedAsGrouped = false;
            try
            {
                if (isImdbGrouped)
                {
                    (anime, imdbHeadSeason, imdbHeadAnilistId) = await _animeMetaLoader.BuildGroupedImdbAnimeAsync(id);
                    renderedAsGrouped = anime != null;
                }
                if (anime == null)
                {
                    id = await _animeMetaLoader.ResolveToServiceIdAsync(id, animeService) ?? id;
                    anime = await LoadAnimeForWatchAsync(id, animeService, tokenData, groupSeasons);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AnimeController.Watch failed (id={Id}).", id);
                Response.StatusCode = 404;
                return View("NotFound");
            }

            // 18+ gate — same as Detail. A deep-link to
            // /anime/{adult}/watch/N must 404 for viewers without the
            // showAdultContent opt-in so the toggle can't be bypassed
            // via the watch URL.
            if (anime != null && anime.isAdult)
            {
                var watchConfig = await GetConfigByUidAsync(uid, _configStore);
                if (watchConfig?.showAdultContent != true)
                {
                    Response.StatusCode = 404;
                    return View("NotFound");
                }
            }

            if (anime?.videos == null || anime.videos.Count == 0)
            {
                // Movie meta path: services collapse the entire feature into a
                // single streamable unit and leave videos empty. Synthesize a
                // single Video for episode 1 so the existing per-episode
                // rendering below works unchanged. Anything other than
                // /watch/1 on a movie 404s — there's no "ep 2 of a movie".
                if (anime != null
                    && episode == 1
                    && anime.type == MetaType.movie.ToString())
                {
                    anime.videos = [new Video
                    {
                        episode = 1,
                        season = 1,
                        title = anime.name,
                        thumbnail = anime.poster ?? anime.background,
                    }];
                }
                else
                {
                    Response.StatusCode = 404;
                    return View("NotFound");
                }
            }
            else if (!renderedAsGrouped)
            {
                // imdb-grouped already carries multi-season videos from
                // Cinemeta's full episode list; NormaliseCourEpisodeNumbering
                // would collapse them to season 1 which defeats the season
                // tab UI on the matching Detail render.
                AnimeMetaLoader.NormaliseCourEpisodeNumbering(anime);
            }

            // Same airing-schedule overlay Detail() does — keeps the Watch
            // page's prev/next nav in sync with the Detail page's click gate
            // so a click via notification can navigate forwards/backwards
            // without the Cinemeta-stale released-date logic skipping over
            // episodes that have actually aired (see Detail() for the full
            // rationale).
            var watchSourceLinks = await _mappingService.BuildSourceLinksAsync(anime.id);
            var watchOverlayAnilistId = imdbHeadAnilistId ?? watchSourceLinks?.AnilistId;
            await _animeMetaLoader.OverlayAniListAiringScheduleAsync(anime, watchOverlayAnilistId, imdbHeadSeason);

            // Match on episode + season — season null means "any cour", which
            // covers the common single-cour case where the videos all carry
            // season 1 implicitly.
            var current = anime.videos.FirstOrDefault(v =>
                v.episode == episode &&
                (season == null || (v.season > 0 ? v.season : 1) == season.Value));
            if (current == null)
            {
                Response.StatusCode = 404;
                return View("NotFound");
            }

            var (prev, next) = ComputePrevNext(anime.videos, current);

            // Stream-addon presence gates the inline player — the source
            // picker still renders below either way (external links
            // for users without addons) but the empty player wrap would
            // be dead chrome if nothing can hand it a playable URL.
            bool hasAddons = false;
            if (!string.IsNullOrEmpty(uid))
            {
                hasAddons = (await _configStore.GetStreamAddonsAsync(uid)).Count > 0;
            }

            return View("Watch", new WatchViewModel
            {
                Anime = anime,
                Current = current,
                Prev = prev,
                Next = next,
                ConfigUid = uid,
                AnonymousUser = tokenData.anonymousUser,
                HasStreamAddons = hasAddons,
            });
        }

        /// <summary>
        /// Slim version of <see cref="Detail"/>'s per-service id dispatch.
        /// Returns the resolved Meta or null when no service knows about
        /// the id. Skips the filler / entry / extras enrichment Detail
        /// adds — the watch page doesn't need any of it.
        /// </summary>
        private async Task<Meta> LoadAnimeForWatchAsync(string id, AnimeService animeService, TokenData tokenData, bool groupSeasons)
        {
            if (id.StartsWith(tmdbPrefix))
                return await _tmdbService.GetAnimeByIdAsync(id, tokenData);

            if (id.StartsWith(kitsuPrefix))
            {
                var a = await _kitsuService.GetAnimeByIdAsync(id, tokenData, groupSeasons: groupSeasons);
                if (a != null) return a;
                var mapping = await _mappingService.GetKitsuMapping(id);
                if (mapping?.AnilistId != null)
                    return await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons: groupSeasons);
                return null;
            }

            if (id.StartsWith(anilistPrefix))
            {
                var a = await _anilistService.GetAnimeByIdAsync(id, tokenData, groupSeasons: groupSeasons);
                if (a != null) return a;
                var mapping = await _mappingService.GetAnilistMapping(id);
                if (mapping?.KitsuId != null)
                    return await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons: groupSeasons);
                return null;
            }

            if (id.StartsWith(malPrefix))
            {
                var a = await _malService.GetAnimeByIdAsync(id, tokenData, groupSeasons: groupSeasons);
                if (a != null) return a;
                var mapping = await _mappingService.GetMalMapping(id);
                if (mapping?.AnilistId != null)
                    return await _anilistService.GetAnimeByIdAsync($"{anilistPrefix}{mapping.AnilistId}", tokenData, groupSeasons: groupSeasons);
                if (mapping?.KitsuId != null)
                    return await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons: groupSeasons);
                return null;
            }

            return null;
        }

        /// <summary>
        /// Resolves the user's real client IP for downstream addon
        /// requests. ASP.NET's ForwardedHeaders middleware
        /// (Program.cs) normally rewrites <c>RemoteIpAddress</c> to
        /// the X-Forwarded-For value when AniSync sits behind a
        /// load balancer / CDN; we also consult the Fly.io- and
        /// Cloudflare-specific headers explicitly as a belt-and-
        /// braces fallback for hosts where the middleware's
        /// known-proxy list doesn't cover the front edge. IPv4-
        /// mapped IPv6 addresses get unwrapped so MediaFusion-style
        /// IP-binding comparisons match the form the user's
        /// browser will reconnect from.
        /// </summary>
        private static string ResolveClientIp(HttpContext ctx)
        {
            string headerIp = null;
            if (ctx.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf) && cf.Count > 0)
                headerIp = cf[0]?.Trim();
            if (string.IsNullOrEmpty(headerIp)
                && ctx.Request.Headers.TryGetValue("Fly-Client-IP", out var fly) && fly.Count > 0)
                headerIp = fly[0]?.Trim();
            if (string.IsNullOrEmpty(headerIp)
                && ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff) && xff.Count > 0)
                headerIp = xff[0]?.Split(',')[0]?.Trim();

            if (!string.IsNullOrEmpty(headerIp)) return headerIp;

            var addr = ctx.Connection.RemoteIpAddress;
            if (addr == null) return null;
            if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
            return addr.ToString();
        }

        /// <summary>
        /// Walks the episode list (in (season, episode) order) and finds the
        /// neighbours of <paramref name="current"/>. Returns (null, null) at
        /// the ends so the view can hide the prev / next buttons cleanly.
        /// Ignores future-dated episodes for the "next" lookup so the user
        /// doesn't land on an unaired episode picker that has nothing to
        /// show.
        /// </summary>
        private static (Video Prev, Video Next) ComputePrevNext(List<Video> videos, Video current)
        {
            var ordered = videos
                .OrderBy(v => v.season > 0 ? v.season : 1)
                .ThenBy(v => v.episode)
                .ToList();
            var idx = ordered.FindIndex(v =>
                v.episode == current.episode &&
                (v.season > 0 ? v.season : 1) == (current.season > 0 ? current.season : 1));
            if (idx < 0) return (null, null);

            Video prev = idx > 0 ? ordered[idx - 1] : null;
            Video next = null;
            for (int i = idx + 1; i < ordered.Count; i++)
            {
                var v = ordered[i];
                if (IsFutureEpisode(v)) continue; // unaired — skip when picking "next"
                next = v;
                break;
            }
            return (prev, next);
        }

        /// <summary>
        /// Shared "has this episode aired yet?" check used by the prev/next
        /// nav on the Watch page and (via the same overlay-populated airingAt
        /// field) the click gate on the Detail page. Prefers AniList's
        /// airingSchedule timestamp when present (community-maintained,
        /// tracks the real-world broadcast) and falls back to the Cinemeta
        /// `released` ISO date when AniList has no schedule for the anime —
        /// older finished shows often don't.
        /// </summary>
        private static bool IsFutureEpisode(Video v)
        {
            if (v == null) return false;
            if (v.airingAt.HasValue)
            {
                return v.airingAt.Value > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            if (!string.IsNullOrEmpty(v.released) && v.released.Length >= 10
                && DateTime.TryParse(v.released[..10], out var d))
            {
                return d.Date > DateTime.UtcNow.Date;
            }
            return false;
        }

        /// <summary>
        /// Episode click endpoint backing the Detail page's stream picker modal.
        /// Returns the union of (a) RD-resolved Torrentio streams when the user
        /// has a Real-Debrid API key configured on a v5 (uid-backed) install
        /// and (b) the same legal external streaming links that
        /// StreamController emits to the Stremio addon. Anonymous / RD-less
        /// users get only the external links. Both sections may be empty;
        /// the view treats an all-empty response as a "no playable sources"
        /// state.
        ///
        /// Query-string route on purpose: the Detail action above is bound to
        /// /anime/{*id} which would otherwise swallow any literal sub-path.
        /// </summary>
        [HttpGet("/anime/episode-streams")]
        public async Task<IActionResult> EpisodeStreams(string id, int? season, int episode, string type = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest(new { error = "id required" });
            }

            // For movie-typed entries we pass null episode + null season to
            // the stream-addon fan-out so BuildStremioId emits the "movie"
            // path shape (imdb / kitsu:N alone) instead of the "series"
            // shape (imdb:S:E) — the latter doesn't match anything on the
            // addon side for a feature film.
            var isMovie = string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase);
            int? lookupEpisode = isMovie ? null : episode;
            int? lookupSeason = isMovie ? null : season;

            var (tokenData, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            tokenData ??= new TokenData { anime_service = AnimeService.Kitsu };

            // Stream addons — one fan-out per configured manifest URL.
            // Anonymous installs and users with no addons see no debrid
            // sources; the source picker falls back to external links
            // only (if those are enabled separately).
            var addons = !string.IsNullOrEmpty(uid)
                ? await _configStore.GetStreamAddonsAsync(uid)
                : new List<StreamAddon>();

            var debridStreams = Array.Empty<object>() as IReadOnlyList<object>;
            if (addons.Count > 0)
            {
                // The 10-minute fan-out reuse window lives in the
                // browser now — the watch page checks
                // localStorage["anisync.streams.{uid}.{episodeKey}"]
                // before calling this endpoint and skips the fetch on a
                // warm hit. Reasoning: the rate-limit signal that the
                // server-side cache was meant to dampen is upstream-
                // side per-id, not pure per-IP, so a shared sqlite row
                // wouldn't actually have helped two users picking the
                // same episode at the same time anyway — only the one
                // who fetched first would benefit. Browser storage
                // keeps the cache personal (no shared lock, no DB
                // round-trip on each render) at the cost of not
                // surviving a different-device refresh — which is the
                // right trade because the addon URL is IP-signed and
                // wouldn't have played from a different device anyway.
                var sourceLinks = await _mappingService.BuildSourceLinksAsync(id);

                // Override sourceLinks.ImdbSeason with the per-call
                // value ONLY when the JS supplied a >1 season. The
                // signal disambiguates two opposite Cinemeta data
                // shapes:
                //
                //   • Multi-season Cinemeta (Naruto): one IMDb id
                //     spans every cour as S1 / S2 / S3 with the cours'
                //     episodes correctly bucketed.
                //     NormaliseCourEpisodeNumbering preserves the
                //     original Cinemeta season (e.g. 3) on each video,
                //     so the JS supplies streamSeason=3. Fribb's
                //     mapping.Season for these shows is often 1 (the
                //     mapping points to the franchise's head cour
                //     rather than the specific cour), so without this
                //     override the query goes to tt0409591:1:100
                //     instead of the correct tt0409591:3:28.
                //
                //   • Single-season Cinemeta with cour-specific IMDb
                //     id (Bookworm S3): Cinemeta serves only the S3
                //     cour's 12 episodes under its IMDb id, all
                //     labelled season=1. After Normalise, imdbSeason=1,
                //     so the JS supplies streamSeason=1. Fribb's
                //     mapping.Season for this MAL/AniList cour is the
                //     authoritative franchise pointer (3) and
                //     BuildSourceLinksAsync already wrote it into
                //     sourceLinks.ImdbSeason. Skipping the override
                //     here keeps the query at :3:2 (matches the
                //     addon's torrent-name index for the S3 cour)
                //     instead of routing to :1:2 — which the user
                //     reported finds streams of S1E2 from the
                //     franchise instead.
                //
                // streamSeason==1 is also the harmless single-cour
                // default — for a genuine S1 cour the mapping.Season
                // is 1 too, so no-op either way.
                if (!isMovie && lookupSeason.HasValue && lookupSeason.Value > 1)
                {
                    sourceLinks.ImdbSeason = lookupSeason.Value;
                }

                // The user's real client IP — forwarded to each addon
                // so playback URLs that bind to the requesting IP (e.g.
                // MediaFusion) sign tokens for the user's IP rather
                // than ours. ForwardedHeaders middleware (Program.cs)
                // already populates RemoteIpAddress from X-Forwarded-
                // For when AniSync sits behind a CDN / load balancer.
                var clientIp = ResolveClientIp(HttpContext);

                // Fan out in parallel — addons are independent
                // endpoints with no shared rate-limit, so total latency
                // floors at max(addon latency) rather than summing.
                // tokenData.anime_service feeds BuildStremioId's
                // third-tier fallback (IMDb → Kitsu → primary tracker
                // id-space) so a show missing both an IMDb and a Kitsu
                // mapping still produces a request the user's addons
                // might know about under mal:N / anilist:N.
                var primaryService = tokenData.anime_service;
                var fetchTasks = addons
                    .Select(a => _addonStreamService.GetStreamsAsync(
                        a.Url, sourceLinks, lookupSeason, lookupEpisode, primaryService, clientIp))
                    .ToArray();
                await Task.WhenAll(fetchTasks);

                // Override each stream's URL-host-derived Provider
                // fallback with the addon's persisted display name
                // (pulled from manifest.name on save), since the
                // addon doesn't echo its name on the /stream
                // response. Preserve the addon's emit order — no
                // post-fetch dedupe, no per-quality cap. Whatever
                // the user's configured addons return (in the
                // order they returned it) is what the source
                // picker shows; filtering / ordering belongs in
                // each addon's own config URL.
                var labelledStreams = new List<AddonStream>();
                for (int i = 0; i < addons.Count; i++)
                {
                    foreach (var s in fetchTasks[i].Result)
                    {
                        labelledStreams.Add(s with { Provider = addons[i].Name });
                    }
                }

                debridStreams = labelledStreams.Select(s => (object)new
                {
                    name = s.Name,
                    title = s.Title,
                    url = s.Url,
                    quality = s.Quality,
                    size = s.Size,
                    playable = s.Playable,
                    seeders = s.Seeders,
                    language = s.Language,
                    provider = s.Provider,
                    isHevc = s.IsHevc,
                    source = s.Source,
                    hdr = s.Hdr,
                    audio = s.Audio,
                }).ToList();
            }

            // External streaming destinations — same per-service dispatch
            // StreamController uses. Series-level (no episode deep-link), so
            // the same list comes back regardless of episode number; the
            // modal still renders them as the fallback bucket. Gated on the
            // user's showExternalStreams toggle so disabling it on
            // /stremio or /advanced hides the Other sites block here too.
            var externalEnabled = false;
            if (!string.IsNullOrEmpty(uid))
            {
                var cfg = await GetConfigByUidAsync(uid, _configStore);
                externalEnabled = cfg?.showExternalStreams == true;
            }
            List<StreamingLink> externalRaw = null;
            if (externalEnabled && !string.IsNullOrEmpty(id))
            {
                externalRaw = tokenData.anime_service switch
                {
                    AnimeService.Anilist     => await _anilistService.GetExternalLinksAsync(id, tokenData),
                    AnimeService.MyAnimeList => await _malService.GetExternalLinksAsync(id, tokenData),
                    _                        => await _kitsuService.GetExternalLinksAsync(id, tokenData),
                };
            }

            var externalLinks = (externalRaw ?? new List<StreamingLink>())
                .Where(l => !string.IsNullOrEmpty(l.Url) && !string.IsNullOrEmpty(l.Site))
                .Select(l => new { site = l.Site, url = l.Url })
                .ToList();

            // Cross-service links + AniSkip both key off the same anime id —
            // fetch the source-links bundle once.
            var sourceLinksForExtras = await _mappingService.BuildSourceLinksAsync(id);

            // AniSkip — same lookup chain StreamController.BuildSkipHintsAsync
            // uses for the Stremio addon side. Returns the intro/outro
            // markers for the resolved MAL id; surfaces silently as null
            // when there's no MAL mapping or no markers.
            object skipTimes = null;
            try
            {
                var malIdRaw = await _mappingService.GetIdByService(id, AnimeService.MyAnimeList, season);
                if (int.TryParse(malIdRaw, out var malId) && malId > 0 && episode > 0)
                {
                    var markers = await _aniSkipService.GetSkipTimesAsync(malId, episode);
                    if (markers != null && markers.Count > 0)
                    {
                        // Multiple "op" variants (op / mixed-op) can exist —
                        // last-wins matches what BuildSkipHintsAsync does.
                        SkipTime intro = null, outro = null;
                        foreach (var m in markers)
                        {
                            switch (m.Type)
                            {
                                case "op": case "mixed-op": intro = m; break;
                                case "ed": case "mixed-ed": outro = m; break;
                            }
                        }
                        skipTimes = new
                        {
                            intro = intro == null ? null : new { start = intro.Start, end = intro.End },
                            outro = outro == null ? null : new { start = outro.Start, end = outro.End },
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AniSkip lookup failed for {Id} ep {Ep}.", id, episode);
            }

            // Subtitles are now fetched lazily by the watch page once
            // the user picks a source — the source's filename is the
            // signal OpenSubtitles needs to return release-matched
            // tracks (correct timing), and we don't know which source
            // the user will pick at this point. See EpisodeSubtitles
            // below. Skipping the upfront fetch keeps this response
            // lighter too.

            return Json(new
            {
                anonymous = tokenData.anonymousUser,
                addonsConfigured = addons.Count > 0,
                debridStreams,
                externalLinks,
                skipTimes,
            });
        }

        /// <summary>
        /// Lazy subtitle lookup invoked by the watch page after the
        /// user picks a source. Splitting this off from
        /// <see cref="EpisodeStreams"/> lets us pass the chosen
        /// source's filename to OpenSubtitles, which is the signal
        /// that selects release-matched subs whose timing actually
        /// matches the file. Best-effort: any failure returns an
        /// empty list so the player initialises without subs rather
        /// than 500ing.
        /// </summary>
        [HttpGet("/anime/episode-subtitles")]
        public async Task<IActionResult> EpisodeSubtitles(string id, int? season, int episode, string filename = null)
        {
            if (string.IsNullOrWhiteSpace(id) || episode <= 0)
            {
                return Json(new { subtitles = Array.Empty<object>() });
            }

            var sourceLinks = await _mappingService.BuildSourceLinksAsync(id);
            if (string.IsNullOrEmpty(sourceLinks.ImdbId))
            {
                // OpenSubtitles is IMDb-keyed via the Stremio addon's
                // series/tt:s:e shape. No IMDb mapping = nothing to ask.
                return Json(new { subtitles = Array.Empty<object>() });
            }

            // ImdbSeason on the mapping is the franchise-side season
            // — same fix as Torrentio. URL season is the AniSync cour-
            // internal value (usually 1) and would query the wrong
            // season of the IMDb listing otherwise.
            var effectiveSeason = sourceLinks.ImdbSeason ?? season;

            var tracks = await SafeOpenSubtitlesSearch(sourceLinks.ImdbId, effectiveSeason, episode, filename, id);

            return Json(new
            {
                subtitles = tracks.Select(t => (object)new
                {
                    lang = t.Lang,
                    label = t.Label,
                    url = t.Url,
                    source = t.Source,
                }).ToList(),
                // Per-provider counts so the UI can surface a
                // "Subs · OS: X" status chip without having to derive
                // the breakdown from track labels.
                providerCounts = new
                {
                    opensubtitles = tracks.Count,
                },
            });
        }

        private async Task<IReadOnlyList<SubtitleTrack>> SafeOpenSubtitlesSearch(
            string imdbId, int? season, int episode, string filename, string id)
        {
            try { return await _subtitleService.SearchAsync(imdbId, season, episode, filename); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenSubtitles search failed for {Id} ep {Ep}.", id, episode);
                return Array.Empty<SubtitleTrack>();
            }
        }

        /// <summary>
        /// Proxies a subtitle URL through our origin and converts SRT to
        /// VTT on the fly. Backs the &lt;track src&gt; tag on the watch
        /// page — without this hop, the &lt;track&gt; load would be
        /// cross-origin and the &lt;video&gt; would need a
        /// <c>crossorigin</c> attribute (which can break some RD URLs
        /// that reject anonymous CORS requests).
        /// </summary>
        [HttpGet("/anime/subtitle")]
        [HttpGet("/anime/subtitle.vtt")]
        public async Task<IActionResult> Subtitle(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return BadRequest();
            }
            return await ServeSubtitleVtt(url);
        }

        /// <summary>
        /// Alternative subtitle proxy route that carries the upstream
        /// URL as a base64url-encoded path segment instead of a query
        /// parameter. Used by the watch page's external-launch flow:
        /// VLC's input-slave subtitle-format detection looks at the
        /// URL's file extension, and "?url=…" at the end of a query-
        /// string-bearing URL means the slave never registers as a
        /// subtitle file (.vtt) at all. This route ends the URL in a
        /// literal "/subtitle.vtt" so the extension check fires
        /// cleanly. The shape of the path is intentional — putting
        /// the encoded payload BEFORE the filename keeps the .vtt as
        /// the very last segment.
        /// </summary>
        [HttpGet("/anime/sub/{encoded}/subtitle.vtt")]
        public async Task<IActionResult> SubtitleByPath(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded)) return BadRequest();
            string upstream;
            try
            {
                // base64url ↔ base64: undo the URL-safe substitutions
                // and restore padding so the .NET decoder accepts it.
                var b64 = encoded.Replace('-', '+').Replace('_', '/');
                var pad = b64.Length % 4;
                if (pad > 0) b64 += new string('=', 4 - pad);
                upstream = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            catch
            {
                return BadRequest();
            }
            if (string.IsNullOrWhiteSpace(upstream)) return BadRequest();
            return await ServeSubtitleVtt(upstream);
        }

        private async Task<IActionResult> ServeSubtitleVtt(string url)
        {
            var vtt = await _subtitleService.FetchAsVttAsync(url);
            if (string.IsNullOrEmpty(vtt))
            {
                return StatusCode(502);
            }
            // 1-hour client cache so re-seeks / re-renders don't refetch.
            Response.Headers["Cache-Control"] = "public, max-age=3600";
            // Allow cross-origin reads so Chromecast's Default Media
            // Receiver (running on a separate gstatic.com origin) can
            // fetch the VTT when the user casts the video. The same
            // header is harmless for the local <video> fetch and for
            // VLC sidecars — both already work without CORS.
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            // Suggest a sensible filename for external players that
            // sniff Content-Disposition (VLC does this when the URL
            // path itself doesn't end in a known subtitle extension).
            // Combined with the /anime/subtitle.vtt path alias this
            // gives VLC two strong format signals so it actually
            // auto-loads our HTTP sidecar instead of fetching and
            // ignoring it.
            Response.Headers["Content-Disposition"] = "inline; filename=\"subtitle.vtt\"";
            return Content(vtt, "text/vtt", System.Text.Encoding.UTF8);
        }

        /// <summary>
        /// Marks an anime episode as watched on the user's primary
        /// tracking service plus any linked secondaries. Called by the
        /// watch page when:
        ///   * the in-player progress crosses 70 % of duration, or
        ///   * the user clicks the "Open with…" button to hand the
        ///     stream off to an external player (we can't see their
        ///     progress after that, so we commit at hand-off time).
        ///
        /// Idempotent — calling it twice for the same id+episode is a
        /// cheap no-op on the tracker side (SaveAnimeEntryAsync writes
        /// the same row). Honours the per-user disableAutoTrack flag:
        /// when set, the endpoint returns 200 with reason=opted-out
        /// and skips the upstream calls entirely. Anonymous and
        /// not-signed-in callers also get a clean reason-coded 200
        /// rather than 401 so the client doesn't surface anything
        /// alarming for users who chose to watch without an account.
        /// </summary>
        [HttpPost("/anime/mark-watched")]
        public async Task<IActionResult> MarkWatched([FromBody] MarkWatchedRequest req)
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Id) || req.Episode <= 0)
            {
                return BadRequest(new { ok = false, reason = "invalid-request" });
            }

            var tokenData = await _tokenService.GetAccessTokenAsync();
            if (tokenData is null || string.IsNullOrWhiteSpace(tokenData.access_token))
            {
                return Json(new { ok = false, reason = "no-auth" });
            }
            if (tokenData.anonymousUser)
            {
                return Json(new { ok = false, reason = "anonymous" });
            }

            // Honour the per-user "Auto-track progress" toggle that
            // already gates the subtitle-driven progress save. Same
            // helper the addon's SubtitlesController uses, but keyed
            // by UID since the web-app session has no decoded config
            // blob — resolve the UID from token identity first.
            try
            {
                var (uid, _) = await _configStore.FindUidByIdentityAsync(tokenData);
                if (!string.IsNullOrEmpty(uid))
                {
                    var cfg = await GetConfigByUidAsync(uid, _configStore);
                    if (cfg?.disableAutoTrack == true)
                    {
                        return Json(new { ok = false, reason = "opted-out" });
                    }
                }
            }
            catch { /* flag read failed — proceed; better to over-track than miss */ }

            // Optional source verification. The external-launcher
            // trigger sends the source URL it's about to hand off
            // so we can probe it BEFORE persisting the mark — the
            // in-app player has no way to verify a cold click on a
            // bad source (no video element yet to check
            // duration < 60s), so the verification happens server-
            // side here. If we detect the RD DMCA placeholder, also
            // mark the hash bad while we're at it so the next list
            // render prunes it.
            if (!string.IsNullOrEmpty(req.SourceUrl))
            {
                if (await LooksLikePlaceholderSourceAsync(req.SourceUrl, HttpContext.RequestAborted))
                {
                    _logger.LogInformation(
                        "Refused mark-watched for {Id} S{Season}E{Episode}: source URL looks like a debrid placeholder.",
                        req.Id, req.Season, req.Episode);
                    return Json(new { ok = false, reason = "placeholder" });
                }
            }

            try
            {
                // Single call into the shared SyncService helper:
                // dispatches to the right primary-tracker SaveAnimeEntry
                // (AniList / MAL / Kitsu by tokenData.anime_service)
                // AND fans out to linked secondaries. Same code path
                // SubtitlesController uses for the Stremio addon's
                // subtitle-fetch auto-track hook.
                await _syncService.SaveProgressAndFanOutAsync(tokenData, req.Id, req.Season, req.Episode);
                _logger.LogInformation(
                    "Marked watched: {Id} S{Season}E{Episode} on {Service}.",
                    req.Id, req.Season, req.Episode, tokenData.anime_service);
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MarkWatched failed for {Id} S{Season}E{Episode}.",
                    req.Id, req.Season, req.Episode);
                return Json(new { ok = false, reason = "save-failed" });
            }
        }

        /// <summary>
        /// Probes a source URL with the same Range 0-0 mechanism
        /// ResolveStream uses, and reports whether the total file
        /// size looks like RD's DMCA placeholder (≤50 MB). Used by
        /// MarkWatched's external-launch path to refuse marking a
        /// known-bad source before the user has had a chance to
        /// see it fail in the in-app player.
        ///
        /// Probe is best-effort: on any HTTP / network failure we
        /// return false (don't block the mark) so transient issues
        /// don't break tracking.
        /// </summary>
        private async Task<bool> LooksLikePlaceholderSourceAsync(string url, CancellationToken ct)
        {
            const long SuspiciouslySmallBytes = 50 * 1024 * 1024;
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(8));

                var client = _httpClientFactory.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                req.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");
                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);

                long? totalSize = null;
                if (res.Content.Headers.ContentRange?.HasLength == true)
                {
                    totalSize = res.Content.Headers.ContentRange.Length;
                }
                else if (res.StatusCode == System.Net.HttpStatusCode.OK
                         && res.Content.Headers.ContentLength.HasValue)
                {
                    totalSize = res.Content.Headers.ContentLength.Value;
                }
                return totalSize.HasValue && totalSize.Value < SuspiciouslySmallBytes;
            }
            catch
            {
                return false; // probe failed — don't block mark on transient issues
            }
        }

        public class MarkWatchedRequest
        {
            public string Id { get; set; }
            public int? Season { get; set; }
            public int Episode { get; set; }
            /// <summary>
            /// Optional source URL. When present, the server probes
            /// the URL with a Range 0-0 request and refuses to mark
            /// if the response looks like a debrid DMCA placeholder
            /// (small total size, typically 30s of "file removed"
            /// video). Lets the external-launch trigger guard
            /// against false-marking when the user clicks Open
            /// with… on a known-bad source before having played it
            /// in-app. The in-player 70 %-progress trigger doesn't
            /// need this because it already checks duration ≥ 60s
            /// client-side.
            /// </summary>
            public string SourceUrl { get; set; }
        }

        /// <summary>
        /// Resolves a Torrentio resolver URL (<c>strem.fun/resolve/…</c>)
        /// to the post-redirect debrid CDN URL that Torrentio's 302
        /// points at. Used by the embedded-subtitle extractor: the
        /// Cloudflare Worker proxy can't hit Torrentio directly (its
        /// own CF WAF rejects worker traffic with a bot challenge),
        /// but the resolved <c>*.real-debrid.com</c> URL has no CF in
        /// front of it. By doing the redirect-follow here on AniSync's
        /// own server — which Torrentio doesn't bot-block — we hand
        /// the client a CDN URL the Worker can stream from.
        /// </summary>
        [HttpGet("/anime/resolve-stream")]
        public async Task<IActionResult> ResolveStream(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                return BadRequest(new { error = "invalid url" });
            }
            // Lock to known resolver / CDN hosts so this endpoint
            // isn't a generic redirect-follower someone could point
            // at internal addresses.
            var host = u.Host.ToLowerInvariant();
            var allowed = host.EndsWith("strem.fun")
                || host.EndsWith("real-debrid.com")
                || host.EndsWith("alldebrid.com")
                || host.EndsWith("debrid-link.com")
                || host.EndsWith("premiumize.me")
                || host.EndsWith("torbox.app")
                || host.EndsWith("offcloud.com")
                // Stremio stream-addon hosts whose /playback URLs
                // resolve through their own infrastructure (rather
                // than redirecting to a debrid CDN). MediaFusion's
                // ElfHosted instance is the most common; the broader
                // .elfhosted.com cover catches Comet etc. hosted there.
                || host.EndsWith("mediafusion.elfhosted.com")
                || host.EndsWith(".elfhosted.com");
            if (!allowed)
            {
                return Forbid();
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                var client = _httpClientFactory.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                // Range to keep the body tiny — we only care about
                // headers + the post-redirect URI. The CDN will still
                // return its real Content-Length / Range metadata.
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                // Most CF-protected resolvers (Torrentio included)
                // reject obvious bot UAs. A plausible browser UA
                // gets us through; we're not impersonating a user,
                // just doing what their browser would do anyway.
                req.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");

                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                // RequestMessage.RequestUri reflects the URL of the
                // *last* request the HttpClient made — i.e. after
                // all redirects when AllowAutoRedirect is true
                // (default). Falls back to the original on any error.
                var finalUrl = res.RequestMessage?.RequestUri?.ToString() ?? url;
                return Json(new { resolvedUrl = finalUrl });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ResolveStream failed for {Url}.", url);
                return Json(new { resolvedUrl = url });
            }
        }

        private async Task<List<Meta>> TryGetRecommendationsAsync(int anilistId, AnimeService translateTo, bool groupSeasons = false)
        {
            try { return await _anilistFallback.GetRecommendationMetasAsync(anilistId, translateTo, groupSeasons); }
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

        // True when the user has at least one stream source wired up — a
        // Real-Debrid API key on file OR the External services toggle
        // turned on. Drives whether episode rows are clickable: if false,
        // clicking would just navigate to a /watch page with no sources
        // to render, which is a dead end, so we render the rows as inert
        // and skip the cursor/keyboard affordances.
        public bool HasStreamSources { get; set; }

        // True when the page is rendering an imdb-grouped franchise that
        // spans more than one cour. The user's tracker entry is per-cour
        // so the hero can't honestly say "Completed · Ep 25/25" — that
        // number would only describe one of several seasons. Drives the
        // view to render a generic "Manage Entry" pill instead of the
        // per-cour status text. Single-cour grouped renders (one
        // mapping) stay on the specific-message path.
        public bool IsMultiSeasonGroup { get; set; }
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

    /// <summary>
    /// View model for the dedicated /anime/{id}/watch/{episode} page.
    /// Carries the resolved anime, the current episode, and pre-computed
    /// prev / next neighbour episodes so the view can render the nav
    /// without re-walking the episode list at request time. Prev / Next
    /// are null at the ends so the view can hide the buttons cleanly.
    /// </summary>
    public class WatchViewModel
    {
        public Meta Anime { get; set; }
        public Video Current { get; set; }
        public Video Prev { get; set; }
        public Video Next { get; set; }
        public string ConfigUid { get; set; }
        public bool AnonymousUser { get; set; }
        // True when the user has at least one Stremio stream addon
        // configured. The watch page renders the inline player only when
        // this is true — External services alone (Crunchyroll / Netflix
        // / …) navigate out via the source picker rows, so the player
        // chrome would be inert dead weight without addon-resolved
        // streams behind it.
        public bool HasStreamAddons { get; set; }
    }
}
