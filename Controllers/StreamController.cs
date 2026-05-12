using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    [ApiController]
    public class StreamController : ControllerBase
    {
        private readonly ITokenService _tokenService;
        private readonly IAnimeMappingService _mappingService;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly IAniSkipService _aniSkipService;
        private readonly IConfigStore _configStore;
        private readonly ITorrentioService _torrentioService;
        private readonly ILogger<StreamController> _logger;

        public StreamController(ITokenService tokenService, IAnimeMappingService mappingService,
            IAnilistService anilistService, IKitsuService kitsuService, IMalService malService,
            IAniSkipService aniSkipService, IConfigStore configStore,
            ITorrentioService torrentioService,
            ILogger<StreamController> logger)
        {
            _tokenService = tokenService;
            _mappingService = mappingService;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _aniSkipService = aniSkipService;
            _configStore = configStore;
            _torrentioService = torrentioService;
            _logger = logger;
        }

        [HttpGet("{config}/stream/{type}/{id}.json")]
        public async Task<JsonResult> GetStreams(string config, string type, string id)
        {
            var empty = new JsonResult(new { streams = Array.Empty<object>() });
            try
            {
                return await GetStreamsInternal(config, type, id, empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stream request failed (id={Id}, type={Type}).", id, type);
                return empty;
            }
        }

        private async Task<JsonResult> GetStreamsInternal(string config, string type, string id, JsonResult empty)
        {
            var tokenData = await _tokenService.GetAccessTokenAsync(config);
            // Hydrates flags from the config store for v5 URLs; v3 (anonymous) carries them inline.
            var configuration = await ResolveConfigAsync(config, _configStore);

            if (!TryParseAnimeId(id, out var animeId, out var season, out var episode))
                return empty;

            var animeService = tokenData?.anime_service ?? AnimeService.Kitsu;
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, animeService);

            var streams = new List<object>();

            if (string.IsNullOrEmpty(resolvedAnimeId))
            {
                return new JsonResult(new { streams });
            }

            // AniSkip lookup once per request — every emitted stream gets the same
            // skipIntro / skipOutro hints, so there's no point fetching per-stream.
            // Returns null when there's no episode, no MAL mapping, or no markers
            // available; the helper folds the result into the per-stream behaviorHints
            // object.
            var skipHints = await BuildSkipHintsAsync(animeId, season, episode);

            // Real-Debrid streams via Torrentio. v1 scope: only for v5
            // (uid-backed) installs, never for v3 anonymous URLs — keeps
            // RD keys out of install URLs that get pasted around.
            // Prepended so debrid sits above Manage Entry + external streams
            // (which keep firing regardless).
            if (!string.IsNullOrEmpty(configuration?.realDebridApiKey)
                && tokenData != null && !tokenData.anonymousUser)
            {
                var sourceLinks = await _mappingService.BuildSourceLinksAsync(animeId);
                var debrid = await _torrentioService.GetStreamsAsync(
                    configuration.realDebridApiKey, sourceLinks, season, episode);
                var rdBingeGroup = $"anisync:rd:{resolvedAnimeId}";
                foreach (var s in debrid)
                {
                    // Prefer Torrentio's own bingeGroup when present (it
                    // already buckets by quality so Stremio auto-plays the
                    // next episode at the same tier); fall back to our
                    // anime-level group otherwise.
                    var bingeGroup = !string.IsNullOrEmpty(s.BingeGroup) ? s.BingeGroup : rdBingeGroup;
                    streams.Add(new
                    {
                        name = s.Name,
                        title = s.Title,
                        url = s.Url,
                        behaviorHints = MergeBehaviorHints(bingeGroup, skipHints),
                    });
                }
            }

            // Manage Entry stream — shown by default for authenticated, non-anonymous users.
            // The configure page's "Manage Entry" toggle stores the negative bit (hideManageEntry)
            // so existing installs keep showing it without a forced re-save.
            if (tokenData != null && !string.IsNullOrWhiteSpace(tokenData.access_token) && !tokenData.anonymousUser
                && configuration?.hideManageEntry != true)
            {
                var query = string.Concat(
                    season.HasValue ? $"?season={season}" : "",
                    episode.HasValue ? (season.HasValue ? $"&episode={episode}" : $"?episode={episode}") : ""
                );
                var manageUrl = $"{Request.Scheme}://{Request.Host}/{config}/Meta/ManageEntry/{animeId}{query}";

                streams.Add(new
                {
                    title = "📝 Manage Entry",
                    externalUrl = manageUrl,
                    behaviorHints = MergeBehaviorHints(bingeGroup: null, skipHints),
                });
            }

            // External streaming destinations (Crunchyroll, Netflix, …) are opt-in via config
            if (configuration?.showExternalStreams == true)
            {
                if (!string.IsNullOrEmpty(resolvedAnimeId))
                {
                    var externalLinks = animeService switch
                    {
                        AnimeService.Anilist => await _anilistService.GetExternalLinksAsync(animeId, tokenData),
                        AnimeService.MyAnimeList => await _malService.GetExternalLinksAsync(animeId, tokenData),
                        _ => await _kitsuService.GetExternalLinksAsync(animeId, tokenData),
                    };

                    // Group all episodes of the same anime so Stremio can advance through them as a binge
                    var bingeGroup = $"anisync:{animeService}:{resolvedAnimeId}";

                    foreach (var link in externalLinks)
                    {
                        streams.Add(new
                        {
                            name = link.Site,
                            title = $"Watch on {link.Site}",
                            externalUrl = link.Url,
                            behaviorHints = MergeBehaviorHints(bingeGroup, skipHints),
                        });
                    }
                }
            }

            return new JsonResult(new { streams });
        }

        /// <summary>
        /// Resolves the supplied animeId+season to a MAL anime id, asks AniSkip for that
        /// episode's markers, and shapes them into the field names Stremio Enhanced
        /// (the main client that auto-skips today) recognises. Returns null when the
        /// chain bails at any step — every caller treats null as "no skip data, just
        /// emit the regular behaviorHints".
        ///
        /// Known limitation: for IMDb-flat shows (e.g. Cinemeta sends one big "Season 1
        /// Episode N" range for a multi-cour franchise), we resolve to the first cour's
        /// MAL id and pass the absolute episode through. AniSkip won't usually find a
        /// match for that since each cour has its own per-cour episode numbering. A
        /// proper fix would mirror MetaController.BuildSeasonsAsync's auto-cour-detect.
        /// </summary>
        private async Task<Dictionary<string, object>> BuildSkipHintsAsync(string animeId, int? season, int? episode)
        {
            if (!episode.HasValue || episode.Value <= 0) return null;

            var malIdRaw = await _mappingService.GetIdByService(animeId, AnimeService.MyAnimeList, season);
            if (!int.TryParse(malIdRaw, out var malId) || malId <= 0) return null;

            var markers = await _aniSkipService.GetSkipTimesAsync(malId, episode.Value);
            if (markers.Count == 0) return null;

            // AniSkip returns multiple types; map them into Stremio Enhanced's expected
            // shape. When the same anime has both an "op" and a "mixed-op" we take
            // whichever lands last (mixed variants tend to be more accurate when they
            // exist) — the simple foreach overwrite handles that without extra logic.
            var hints = new Dictionary<string, object>();
            foreach (var m in markers)
            {
                switch (m.Type)
                {
                    case "op":
                    case "mixed-op":
                        hints["skipIntro"] = new { start = m.Start, end = m.End };
                        break;
                    case "ed":
                    case "mixed-ed":
                        hints["skipOutro"] = new { start = m.Start, end = m.End };
                        break;
                    case "recap":
                        hints["skipRecap"] = new { start = m.Start, end = m.End };
                        break;
                }
            }
            return hints.Count > 0 ? hints : null;
        }

        /// <summary>
        /// Builds a single behaviorHints object combining whatever bingeGroup the caller
        /// has and whatever skip hints we have. Returns null when neither is set so the
        /// stream JSON stays compact.
        /// </summary>
        private static object MergeBehaviorHints(string bingeGroup, Dictionary<string, object> skipHints)
        {
            if (string.IsNullOrEmpty(bingeGroup) && (skipHints == null || skipHints.Count == 0))
                return null;

            var dict = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(bingeGroup)) dict["bingeGroup"] = bingeGroup;
            if (skipHints != null)
                foreach (var kv in skipHints) dict[kv.Key] = kv.Value;
            return dict;
        }
    }
}
