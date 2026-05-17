using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;

namespace AnimeList.Services
{
    /// <summary>
    /// MyAnimeList REST client. The shape closely mirrors <see cref="KitsuService"/> —
    /// MAL exposes the same conceptual surface (user list, anime detail, search,
    /// seasonal, ranking) but the responses are paged differently and there's no
    /// native airing-schedule endpoint, so we delegate that one to <see cref="IAnilistFallback"/>.
    /// </summary>
    public class MalService : IMalService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly IAnilistFallback _anilistFallback;
        private readonly IConfiguration _configuration;
        private readonly IKitsuService _kitsuService;
        private readonly ICinemetaService _cinemetaService;
        private readonly ILogger<MalService> _logger;

        private const string MalApi = "https://api.myanimelist.net/v2";

        // MAL caps anime list responses at 100 per page; pick a moderate value for parity
        // with the other services.
        private const int CatalogPageSize = 50;
        // The full-fetch loop for user lists uses MAL's max page size to keep the number of
        // round-trips down — power users with hundreds of entries finish in one or two calls.
        private const int FullFetchPageSize = 1000;

        // List statuses the user owns — anything else is a discovery / public catalog.
        private static readonly HashSet<ListType> _userLists =
        [
            ListType.Current, ListType.Completed,
            ListType.Planning, ListType.Paused, ListType.Dropped, ListType.Repeating,
        ];

        // Standard list of fields we ask MAL to include on anime nodes returned in
        // list-style endpoints. Picked to match what Kitsu/AniList already surface.
        // MAL's `fields` parameter is flat — anime fields come back inside each entry's
        // `node` object automatically, no `node{...}` wrapper.
        private const string NodeFields = "id,title,main_picture,media_type,status,num_episodes,mean,start_season,genres,synopsis,alternative_titles";
        // /users/@me/animelist returns the user's list metadata in `list_status`.
        private const string UserListStatusFields = "list_status{status,score,num_episodes_watched,is_rewatching,num_times_rewatched,start_date,finish_date,comments}";
        // /anime/{id} returns the same data in `my_list_status` when authenticated.
        private const string MyListStatusFields = "my_list_status{status,score,num_episodes_watched,is_rewatching,num_times_rewatched,start_date,finish_date,comments}";

        public MalService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService,
            IAnilistFallback anilistFallback, IConfiguration configuration, IKitsuService kitsuService,
            ICinemetaService cinemetaService, ILogger<MalService> logger)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _anilistFallback = anilistFallback;
            _configuration = configuration;
            _kitsuService = kitsuService;
            _cinemetaService = cinemetaService;
            _logger = logger;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null, bool groupSeasons = true, string season = null, bool hideUnreleased = false)
        {
            // MAL has no airing schedule endpoint, so use the AniList fallback like Kitsu does.
            // genre passes through so Airing-by-genre swaps to the RELEASING+genre query.
            if (list == ListType.Airing)
                return await _anilistFallback.GetAiringScheduleAsync(AnimeService.MyAnimeList, skip, genre);

            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.MyAnimeList);
            var isUserList = !list.HasValue || _userLists.Contains(list.Value);

            // User-list endpoints require a logged-in user.
            if (isUserList && string.IsNullOrEmpty(tokenData?.access_token))
                return [];

            // Single-anime user-list lookup: MAL's /users/@me/animelist endpoint can't filter
            // by anime id, so hit the anime detail with my_list_status and synthesize the result.
            if (isUserList && !string.IsNullOrEmpty(resolvedAnimeId))
                return await GetSingleUserListEntryAsync(resolvedAnimeId, tokenData, groupSeasons);

            // For "browse my list" with grouping on we walk every page server-side and dedup
            // across all of them, then UserListCache caches the deduped result so each
            // Stremio re-render is free. With grouping off the manifest declares the `skip`
            // extra so Stremio paginates — one upstream page per request, no global dedup
            // pass needed (every entry already has a unique native id when grouping is off).
            var fetchAll = isUserList && groupSeasons;
            // MAL caps animelist at 1000/page, so a single round-trip covers most users; the
            // ranking/seasonal/search endpoints stay at CatalogPageSize.
            var pageSize = fetchAll ? FullFetchPageSize : CatalogPageSize;

            await _mappingService.EnsureLoadedAsync();
            var seenIds = new Dictionary<string, Meta>();
            int apiOffset = 0;

            while (true)
            {
                var apiSkip = fetchAll ? apiOffset.ToString() : skip;
                var url = BuildListUrl(list, apiSkip, genre, search, sort, isUserList, pageSize, season);
                var json = await GetJsonAsync(url, tokenData);
                if (json == null) break;

                if (json["data"] is not JArray dataArr || dataArr.Count == 0) break;

                foreach (var raw in dataArr.OfType<JObject>())
                {
                    // Both list-style and ranking-style responses wrap the anime in a `node` field;
                    // user-list responses also include a sibling `list_status` block.
                    var node = raw["node"] as JObject;
                    if (node == null) continue;

                    var listStatus = raw["list_status"] as JObject;

                    // Repeating is fetched without any status filter (see BuildListUrl) and
                    // narrowed here on is_rewatching=true. Current is fetched with status=watching
                    // and we additionally drop is_rewatching=true entries so the same anime can't
                    // surface in both Currently Watching and Rewatching when a user has it marked
                    // as both watching and rewatching simultaneously.
                    if (isUserList && list.HasValue)
                    {
                        var isRewatching = (bool?)listStatus?["is_rewatching"] ?? false;
                        if (list == ListType.Repeating && !isRewatching) continue;
                        if (list == ListType.Current && isRewatching) continue;
                    }

                    // MAL doesn't expose per-anime "currently airing vs not yet released" filtering on
                    // the list endpoint — drop pre-release entries from the Currently Watching and
                    // Rewatching catalogs so they match the parity behaviour of the other services.
                    var status = (string)node["status"];
                    if (hideUnreleased && (list == ListType.Current || list == ListType.Repeating) && status == "not_yet_aired")
                        continue;

                    // Genre post-filter — MAL's list/ranking endpoints have no server-side genre
                    // arg, so filter the response. Skip for Seasonal because the catalog repurposes
                    // the `genre` extra as a season selector ("This Season" / "Next Season" / etc.)
                    // and matching that against an anime's genres array would zero out the catalog.
                    // Genre is a real filter for every list type EXCEPT when
                    // it's the Stremio season-selector overload on Seasonal
                    // ("This Season" / "Next Season" / "Previous Season"). In
                    // that one case it's not a genre at all, so skip the
                    // filter — otherwise the post-fetch loop would reject
                    // every entry for not having a genre called "This Season".
                    var isSeasonSelector = list == ListType.Seasonal
                        && !string.IsNullOrEmpty(genre)
                        && SeasonOptions.Contains(genre);
                    if (!isSeasonSelector && !string.IsNullOrEmpty(genre) && !MatchesGenre(node, genre))
                        continue;

                    // Season post-filter for Search — MAL's /anime?q= endpoint
                    // has no server-side season filter, so when the form
                    // combines "search + season" we trim the response
                    // client-side. Compares the entry's start_season to the
                    // resolved (season, year) pair.
                    if (list == ListType.Search && !string.IsNullOrEmpty(season))
                    {
                        var (wantedSeason, wantedYear) = GetSeasonAndYear(season);
                        var entryStart = node["start_season"];
                        var entrySeason = (string)entryStart?["season"];
                        var entryYear = (int?)entryStart?["year"];
                        if (entryYear != wantedYear ||
                            !string.Equals(entrySeason, wantedSeason, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    var meta = await BuildMetaAsync(node, listStatus, groupSeasons);
                    if (meta == null) continue;

                    if (seenIds.TryGetValue(meta.id, out var existing))
                    {
                        if (!string.IsNullOrEmpty(meta.name)
                            && (string.IsNullOrEmpty(existing.name) || meta.name.Length < existing.name.Length))
                            seenIds[meta.id] = meta;
                        continue;
                    }
                    seenIds[meta.id] = meta;
                }

                if (!fetchAll) break;
                if (dataArr.Count < pageSize) break;
                apiOffset += pageSize;
            }

            // Sort user libraries alphabetically by name so franchise cours sit next to each
            // other ("Show", "Show Part 2", "Show Season 2", …) — only meaningful when we
            // have the whole library in memory (grouping on). With grouping off we return
            // a single upstream page so the sort would only reorder within that window and
            // make the global order jagged across pages; keep MAL's own list_updated_at
            // ordering instead. Discovery catalogs always keep their API ranking.
            if (isUserList && fetchAll)
                return seenIds.Values
                    .OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            return seenIds.Values.ToList();
        }

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData, bool groupSeasons = true)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(id, AnimeService.MyAnimeList);
            if (string.IsNullOrEmpty(resolvedAnimeId)) return null;

            // `videos` carries promo/PV YouTube URLs when MAL has them — we use the first one
            // as the trailer and fall back to AniList through the mapping otherwise.
            const string fields =
                "id,title,alternative_titles,main_picture,pictures,synopsis,mean,media_type,status," +
                "num_episodes,start_season,broadcast,source,genres,studios,recommendations,related_anime,videos";

            var json = await GetJsonAsync($"{MalApi}/anime/{resolvedAnimeId}?fields={fields}", tokenData);
            if (json == null) return null;

            var mapping = await _mappingService.GetMalMapping($"{malPrefix}{resolvedAnimeId}");

            // Same toggle as the catalog path: when grouping is on, prefer cross-service ids
            // so meta.id matches what the user clicked from a grouped catalog. When off, keep
            // the response in this service's native id space.
            // videoId will still use the groupId since it is better source for streams.
            var (externalId, groupId, hasGroupId) = ResolveGroupedId(
                mapping, $"{malPrefix}{(string)json["id"]}", groupSeasons, allowKitsuFallback: true);

            var mediaType = (string)json["media_type"];
            var isMovie = IsMovieFormat(mediaType);

            // Mirror the catalog Meta builder so /anime/{id} renders consistent
            // hero chrome — score badge, "TV · 13 eps · 2026" info row.
            var meanScore = (double?)json["mean"];
            // Renamed from `episodeCount` to avoid shadowing the same name
            // declared in the synthetic-videos fallback block further down
            // in this method.
            var numEps = (int?)json["num_episodes"];
            int? releaseYear = null;
            var startSeason = json["start_season"];
            if (startSeason != null && startSeason.Type != JTokenType.Null)
                releaseYear = (int?)startSeason["year"];

            var anime = new Meta((string)json["synopsis"])
            {
                id = externalId,
                type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = ExtractTitle(json),
                poster = SafeGet<string>(json, "main_picture", "large") ?? SafeGet<string>(json, "main_picture", "medium"),
                background = SafeGet<string>((json["pictures"] as JArray)?.OfType<JObject>().FirstOrDefault(), "large")
                             ?? SafeGet<string>(json, "main_picture", "large"),
                genres = (json["genres"] as JArray)?
                    .OfType<JObject>()
                    .Select(g => (string)g["name"])
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList(),
                score = meanScore > 0 ? Math.Round(meanScore.Value, 1) : (double?)null,
                episodes = numEps > 0 ? numEps : null,
                year = releaseYear,
                format = NormalizeFormat(mediaType),
                airStatus = NormalizeAirStatus((string)json["status"]),
                source = NormalizeSource((string)json["source"]),
            };

            // Trailer: try MAL's own `videos` first (rare but present on some titles), then
            // fall back through the mapping to AniList's `trailer` field. Either way we keep
            // the result tightly scoped to YouTube ids since that's all Stremio renders.
            var trailerId = ExtractMalYoutubeTrailerId(json);
            if (string.IsNullOrEmpty(trailerId) && mapping?.AnilistId != null)
            {
                try { trailerId = await _anilistFallback.GetYoutubeTrailerIdAsync(mapping.AnilistId.Value); }
                catch { /* best-effort enrichment */ }
            }
            if (!string.IsNullOrEmpty(trailerId))
            {
                anime.trailers.Add(new Trailer(trailerId));
                anime.trailerStreams.Add(new TrailerStream(trailerId, anime.name));
            }

            if (json["studios"] is JArray studios)
            {
                foreach (var s in studios.OfType<JObject>())
                {
                    var name = (string)s["name"];
                    var sid = (int?)s["id"];
                    if (string.IsNullOrEmpty(name)) continue;
                    anime.links.Add(new Link
                    {
                        name = name,
                        category = "Studio",
                        url = sid.HasValue ? $"https://myanimelist.net/anime/producer/{sid.Value}" : null,
                    });
                }
            }

            if (json["recommendations"] is JArray recs)
            {
                foreach (var r in recs.OfType<JObject>())
                {
                    var rec = r["node"] as JObject;
                    if (rec == null) continue;
                    var name = (string)rec["title"];
                    var rid = (int?)rec["id"];
                    if (string.IsNullOrEmpty(name) || !rid.HasValue) continue;
                    anime.links.Add(new Link
                    {
                        name = name,
                        category = "Similar",
                        url = $"https://myanimelist.net/anime/{rid.Value}",
                    });

                    // Slim Meta for the detail-page recommendations carousel —
                    // same shape AniList produces. MAL's recommendations sub-
                    // node is fixed (id + title + main_picture); score / year
                    // / format / episodes aren't available here, so the
                    // carousel cards render poster + title only. _PosterGrid
                    // handles missing fields gracefully (info row just skips).
                    var recPoster = SafeGet<string>(rec, "main_picture", "large")
                                    ?? SafeGet<string>(rec, "main_picture", "medium");
                    anime.recommendations.Add(new Meta
                    {
                        id = $"{malPrefix}{rid.Value}",
                        name = name,
                        poster = recPoster,
                        type = MetaType.series.ToString(),
                    });
                }
            }

            if (!isMovie)
            {
                // MAL has no per-episode endpoint at all (only num_episodes), so prefer
                // Cinemeta whenever we have an IMDb mapping. Falls back to Kitsu through
                // the cross-service mapping, then to a plain Episode-N synthetic list.
                // try/catch because a malformed mapping (Season parser, etc.) shouldn't
                // take the meta page down.
                if (!string.IsNullOrEmpty(mapping?.ImdbId))
                {
                    try
                    {
                        var malIdInt = int.TryParse(resolvedAnimeId, out var mid) ? mid : 0;
                        var currentEpisodeCount = (int?)json["num_episodes"] ?? 0;
                        anime.videos = await _cinemetaService.GetCourEpisodesAsync(
                            mapping.ImdbId, mapping.Season, AnimeService.MyAnimeList,
                            malIdInt, currentEpisodeCount, GetAnimeSummaryAsync);
                    }
                    catch
                    {
                        anime.videos = [];
                    }
                }

                if (anime.videos.Count == 0 && mapping?.KitsuId != null)
                {
                    var fallback = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", tokenData, groupSeasons);
                    anime.videos = fallback?.videos ?? [];
                }

                if (anime.videos.Count == 0)
                {
                    var episodeCount = (int?)json["num_episodes"] ?? 0;
                    for (int i = 1; i <= episodeCount; i++)
                    {
                        anime.videos.Add(new Video
                        {
                            id = hasGroupId ? $"{groupId}:{mapping.Season ?? 1}:{i}" : $"{groupId}:{i}",
                            title = $"Episode {i}",
                            season = mapping.Season ?? 1,
                            episode = i,
                        });
                    }
                }

                // Normalise every video id to the meta's external id space — the Kitsu
                // fallback above leaves kitsu-prefixed ids attached, which Stremio rejects
                // because they don't share a prefix with meta.id (renders as a blank page).
                NormalizeVideoIds(anime.videos, groupId, hasGroupId);
            }

            // links must be valid or stremio throws error and page can't render. 
            anime.links = anime.links.Where(w => IsValidUrl(w.url)).ToList();

            _logger.LogInformation(
                "MAL GetAnimeByIdAsync: id={Id} resolvedMal={Resolved} mediaType={MediaType} numEpisodes={NumEpisodes} " +
                "mappingHasImdb={HasImdb} mappingHasKitsu={HasKitsu} mappingHasAnilist={HasAnilist} mappingSeason={Season} " +
                "name={Name} videos={VideoCount}",
                id, resolvedAnimeId, mediaType, (int?)json["num_episodes"],
                !string.IsNullOrEmpty(mapping?.ImdbId), mapping?.KitsuId != null, mapping?.AnilistId != null, mapping?.Season,
                anime.name, anime.videos.Count);

            return anime;
        }

        public async Task<(string? name, int? episodeCount)> GetAnimeSummaryAsync(string id)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(id, AnimeService.MyAnimeList);
            if (string.IsNullOrEmpty(resolvedAnimeId)) return (null, null);

            var json = await GetJsonAsync($"{MalApi}/anime/{resolvedAnimeId}?fields=title,num_episodes,alternative_titles", null);
            if (json == null) return (null, null);

            var name = ExtractTitle(json);
            var episodeCount = (int?)json["num_episodes"];
            return (name, episodeCount);
        }

        public async Task<List<StreamingLink>> GetExternalLinksAsync(string animeId, TokenData tokenData)
        {
            // MyAnimeList's API has no streaming-link field, so cross-service over to AniList
            // through the mapping. This gives MAL users the same Crunchyroll/Netflix/HiDive
            // entries the other services surface in Stremio's external streams.
            var anilistIdRaw = await _mappingService.GetIdByService(animeId, AnimeService.Anilist);
            if (!int.TryParse(anilistIdRaw, out var anilistId)) return [];

            try { return await _anilistFallback.GetExternalLinksAsync(anilistId); }
            catch { return []; }
        }

        public async Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null)
        {
            var resolvedMalId = await _mappingService.GetIdByService(animeId, AnimeService.MyAnimeList, season);
            if (string.IsNullOrEmpty(resolvedMalId)) return null;

            var entry = new AnimeEntry { MediaId = resolvedMalId };

            var json = await GetJsonAsync(
                $"{MalApi}/anime/{resolvedMalId}?fields=num_episodes,my_list_status{{status,score,num_episodes_watched,is_rewatching,num_times_rewatched,start_date,finish_date,comments}}",
                tokenData);
            if (json == null) return entry;

            entry.TotalEpisodes = (int?)json["num_episodes"];
            if (entry.TotalEpisodes == 0) entry.TotalEpisodes = null;

            // my_list_status is only present when the request was authenticated AND the user
            // has the anime on their list — otherwise the field is omitted entirely.
            if (json["my_list_status"] is JObject mls)
            {
                entry.EntryId = resolvedMalId; // MAL keys list entries by anime id
                var rawStatus = (string)mls["status"];
                var isRewatching = (bool?)mls["is_rewatching"] ?? false;
                // Surface "rewatching" as a synthetic status so the UI dropdown can offer it
                // alongside the real ones — mirrors AniList's REPEATING.
                entry.Status = isRewatching ? "rewatching" : rawStatus;
                entry.Progress = (int?)mls["num_episodes_watched"] ?? 0;
                // MAL returns 0 for "no rating set" instead of null. Treat it as null so the
                // Manage Entry input renders empty (and doesn't propagate a 0 through the
                // sync fan-out — Kitsu's ratingTwenty has a hard minimum of 2 and 422s on 0).
                entry.Score = NullableScore((double?)mls["score"]);
                entry.Notes = (string)mls["comments"];
                entry.RewatchCount = (int?)mls["num_times_rewatched"] ?? 0;
                entry.StartedAt = ParseProviderDate((string)mls["start_date"]);
                entry.FinishedAt = ParseProviderDate((string)mls["finish_date"]);
            }

            return entry;
        }

        public async Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null)
        {
            if (string.IsNullOrEmpty(tokenData?.access_token))
            {
                _logger.LogWarning("MAL save skipped — token has no access_token (animeId={AnimeId}).", animeId);
                return;
            }

            var resolvedMalId = await _mappingService.GetIdByService(animeId, AnimeService.MyAnimeList, season);
            if (string.IsNullOrEmpty(resolvedMalId))
            {
                _logger.LogWarning("MAL save skipped — no MAL mapping for animeId={AnimeId} season={Season}.", animeId, season);
                return;
            }

            // If the caller didn't specify a status (e.g. subtitle-driven auto-progress), look
            // up the current entry: existing rows keep their status, new rows default to
            // "watching" so an episode-played event creates a sensible list entry instead of
            // MAL's default "plan_to_watch".
            if (string.IsNullOrEmpty(status))
            {
                var existing = await GetAnimeEntryAsync(tokenData, $"{malPrefix}{resolvedMalId}", null);
                status = existing?.Status;
                if (string.IsNullOrEmpty(status))
                    status = "watching";
            }

            // Translate the synthetic "rewatching" status the UI may send back into the
            // (status=watching, is_rewatching=true) pair MAL actually accepts. Real MAL
            // statuses are passed through as-is.
            bool? isRewatching = null;
            if (string.Equals(status, "rewatching", StringComparison.OrdinalIgnoreCase))
            {
                status = "watching";
                isRewatching = true;
            }
            else
            {
                isRewatching = false;
            }

            var fields = new List<KeyValuePair<string, string>>
            {
                new("num_watched_episodes", progress.ToString()),
            };
            if (!string.IsNullOrEmpty(status))
                fields.Add(new("status", status));
            if (isRewatching.HasValue)
                fields.Add(new("is_rewatching", isRewatching.Value ? "true" : "false"));
            if (score.HasValue)
                fields.Add(new("score", ((int)Math.Round(Math.Clamp(score.Value, 0, 10))).ToString()));
            if (notes != null)
                fields.Add(new("comments", notes));
            if (rewatchCount.HasValue)
                fields.Add(new("num_times_rewatched", rewatchCount.Value.ToString()));
            if (startedAt.HasValue)
                fields.Add(new("start_date", startedAt.Value.ToString("yyyy-MM-dd")));
            if (finishedAt.HasValue)
                fields.Add(new("finish_date", finishedAt.Value.ToString("yyyy-MM-dd")));

            var request = new HttpRequestMessage(HttpMethod.Patch, $"{MalApi}/anime/{resolvedMalId}/my_list_status")
            {
                Content = new FormUrlEncodedContent(fields),
            };
            ApplyAuth(request, tokenData);

            var saveResponse = await _clientFactory.CreateClient().SendAsync(request);
            await EnsureSuccessOrThrow(saveResponse, "MyAnimeList", "save");
        }

        public async Task DeleteAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null)
        {
            if (string.IsNullOrEmpty(tokenData?.access_token)) return;

            var resolvedMalId = await _mappingService.GetIdByService(animeId, AnimeService.MyAnimeList, season);
            if (string.IsNullOrEmpty(resolvedMalId)) return;

            var request = new HttpRequestMessage(HttpMethod.Delete, $"{MalApi}/anime/{resolvedMalId}/my_list_status");
            ApplyAuth(request, tokenData);
            var deleteResponse = await _clientFactory.CreateClient().SendAsync(request);
            await EnsureSuccessOrThrow(deleteResponse, "MyAnimeList", "delete");
        }

        private async Task<Meta> BuildMetaAsync(JObject node, JObject listStatus, bool groupSeasons = true)
        {
            var malIdRaw = node["id"];
            if (malIdRaw == null) return null;
            var malIdStr = (string)malIdRaw;

            var mapping = await _mappingService.GetMalMapping($"{malPrefix}{malIdStr}");

            // groupSeasons=true → fall through to IMDb / TMDB before the native MAL id so
            // multiple cours of a franchise collapse to one card via the dedup step in the
            // caller. When the user disables grouping, every cour gets its own native id and
            // dedup is a no-op since native ids don't collide.
            var (externalId, _, _) = ResolveGroupedId(
                mapping, $"{malPrefix}{malIdStr}", groupSeasons, allowKitsuFallback: false);

            var mediaType = (string)node["media_type"];
            var isMovie = IsMovieFormat(mediaType);

            // StreamD-style card chrome: score badge + format/eps/year info row.
            // MAL's mean is already 0-10 (one decimal), num_episodes is direct, and
            // start_season is a nested object whose year we lift inline. NormalizeFormat
            // maps "tv"/"movie"/"ova"/etc. to display labels.
            var meanScore = (double?)node["mean"];
            var episodeCount = (int?)node["num_episodes"];
            int? releaseYear = null;
            var startSeason = node["start_season"];
            if (startSeason != null && startSeason.Type != JTokenType.Null)
                releaseYear = (int?)startSeason["year"];

            return new Meta((string)node["synopsis"])
            {
                id = externalId,
                type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = ExtractTitle(node),
                poster = SafeGet<string>(node, "main_picture", "large") ?? SafeGet<string>(node, "main_picture", "medium"),
                entryId = listStatus != null ? malIdStr : null,
                entryStatus = (string)listStatus?["status"],
                score = meanScore > 0 ? Math.Round(meanScore.Value, 1) : (double?)null,
                episodes = episodeCount > 0 ? episodeCount : null,
                year = releaseYear,
                format = NormalizeFormat(mediaType),
                // num_episodes_watched is the user's progress on MAL — only present
                // on user-list responses (listStatus is null on Trending / Seasonal /
                // search). Cards render the "Ep N / Total" badge when set.
                progress = (int?)listStatus?["num_episodes_watched"],
            };
        }

        private string BuildListUrl(ListType? list, string skip, string genre, string search, string sort, bool isUserList, int pageSize, string season = null)
        {
            string url;
            var offset = int.TryParse(skip, out var s) ? s : 0;
            // MAL accepts the fields parameter unencoded (its docs use raw braces and commas);
            // running it through Uri.EscapeDataString turns `{` into `%7B` and the API silently
            // returns only default fields, which is why genres / status went missing.
            var userListFields = $"{NodeFields},{UserListStatusFields}";

            if (isUserList)
            {
                // Repeating is special: MAL keeps rewatching entries at their original status
                // (typically "completed") with is_rewatching=true rather than introducing a
                // dedicated status. Constraining the wire request to status=watching would
                // miss those entries entirely, so we omit the status filter for Repeating
                // and let the post-fetch filter in GetAnimeListAsync narrow on is_rewatching.
                var statusFilter = list.HasValue && list != ListType.Repeating
                    ? $"&status={MalUserListStatus(list.Value)}" : "";
                url = $"{MalApi}/users/@me/animelist?fields={userListFields}&sort=list_updated_at{statusFilter}";
            }
            else if (list == ListType.Seasonal)
            {
                // Same dual-input shape AnilistService uses: explicit
                // `season` ("Spring 2026") wins over the Stremio legacy
                // `genre`-as-season overload ("This Season" / "Next Season"
                // / "Previous Season"); anything else in `genre` is a real
                // genre filter applied client-side via MatchesGenre below.
                var seasonSelector = !string.IsNullOrEmpty(season)
                    ? season
                    : (!string.IsNullOrEmpty(genre) && SeasonOptions.Contains(genre) ? genre : SeasonCurrent);
                var (resolvedSeason, year) = GetSeasonAndYear(seasonSelector);
                var seasonLower = resolvedSeason.ToLowerInvariant();
                // Default to popularity-descending (matching AniList POPULARITY_DESC and
                // Kitsu -userCount) so the catalog leads with the season's biggest titles.
                var sortValue = string.IsNullOrEmpty(sort) ? "anime_num_list_users" : SeasonalSortToMal(sort);
                url = $"{MalApi}/anime/season/{year}/{seasonLower}?fields={NodeFields}&sort={sortValue}";
            }
            else if (list == ListType.Search)
            {
                var q = string.IsNullOrEmpty(search) ? string.Empty : search;
                url = $"{MalApi}/anime?q={Uri.EscapeDataString(q)}&fields={NodeFields}";
            }
            else
            {
                // Trending / fallback: the ranking endpoint exposes a popularity bucket plus a
                // few alternates (airing, all). The user's sort choice picks the bucket.
                var rankingType = string.IsNullOrEmpty(sort) ? "bypopularity" : SortToMal(sort);
                url = $"{MalApi}/anime/ranking?ranking_type={rankingType}&fields={NodeFields}";
            }

            url += $"&limit={pageSize}";
            if (offset > 0) url += $"&offset={offset}";
            return url;
        }

        /// <summary>
        /// Fetches a single anime's detail with the caller's list status attached, returning a
        /// one-element Meta list when the user has the anime on their list (or empty otherwise).
        /// MAL doesn't have an animelist?anime_id filter, so this is the only round-trip that
        /// gives us "is this on the user's list, and at what status?" for a known anime.
        /// </summary>
        private async Task<List<Meta>> GetSingleUserListEntryAsync(string resolvedMalId, TokenData tokenData, bool groupSeasons = true)
        {
            // The detail endpoint returns the user's list metadata under `my_list_status` (not
            // `list_status` like the user-list endpoint), so the fields spec uses MyListStatusFields.
            var json = await GetJsonAsync(
                $"{MalApi}/anime/{resolvedMalId}?fields={NodeFields},{MyListStatusFields}",
                tokenData);
            if (json == null) return [];

            var listStatus = json["my_list_status"] as JObject;
            if (listStatus == null) return [];

            await _mappingService.EnsureLoadedAsync();
            var meta = await BuildMetaAsync(json, listStatus, groupSeasons);
            return meta == null ? [] : [meta];
        }

        /// <summary>
        /// Picks the MAL status string for the user-list endpoint. Repeating reuses watching
        /// (filtered post-fetch by is_rewatching) since MAL has no separate rewatching status.
        /// </summary>
        private static string MalUserListStatus(ListType list) => list switch
        {
            ListType.Current => "watching",
            ListType.Repeating => "watching",
            ListType.Completed => "completed",
            ListType.Planning => "plan_to_watch",
            ListType.Paused => "on_hold",
            ListType.Dropped => "dropped",
            _ => "watching",
        };

        /// <summary>
        /// Maps a UI sort label to the seasonal endpoint's <c>sort</c> parameter. MAL's seasonal
        /// sort values are different from the ranking endpoint's, so we keep them in their own table.
        /// </summary>
        private static string SeasonalSortToMal(string sort) => sort switch
        {
            SortScore => "anime_score",
            SortRecent => "anime_start_date",
            _ => "anime_num_list_users",
        };

        private async Task<JObject> GetJsonAsync(string url, TokenData tokenData)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, tokenData);

            var response = await _clientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            return JObject.Parse(content);
        }

        /// <summary>
        /// MAL requires either an OAuth Bearer token or an X-MAL-CLIENT-ID header on every
        /// request. We send the bearer if we have one (authenticated reads/writes) and fall
        /// back to the client id for anonymous public reads (Trending, Seasonal, Search).
        /// </summary>
        private void ApplyAuth(HttpRequestMessage request, TokenData tokenData)
        {
            if (!string.IsNullOrEmpty(tokenData?.access_token))
            {
                ApplyBearerAuth(request, tokenData);
                return;
            }

            var clientId = _configuration["Mal:ClientId"];
            if (!string.IsNullOrEmpty(clientId))
                request.Headers.TryAddWithoutValidation("X-MAL-CLIENT-ID", clientId);
        }

        private static bool MatchesGenre(JObject node, string genre)
        {
            if (node["genres"] is not JArray genres) return false;
            foreach (var g in genres.OfType<JObject>())
            {
                if (string.Equals((string)g["name"], genre, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string ExtractTitle(JObject anime)
        {
            // Preferred-to-fallback chain. en is the English-localised
            // title, title is MAL's canonical (often romaji), ja is the
            // Japanese script, synonyms[0] is the broadest catch — some
            // user-list entries come back with all three primary fields
            // empty and only a synonyms array attached, which is what
            // produced the title-less cards on /library.
            var en = SafeGet<string>(anime, "alternative_titles", "en");
            if (!string.IsNullOrWhiteSpace(en)) return en;
            var canonical = (string)anime["title"];
            if (!string.IsNullOrWhiteSpace(canonical)) return canonical;
            var ja = SafeGet<string>(anime, "alternative_titles", "ja");
            if (!string.IsNullOrWhiteSpace(ja)) return ja;
            var synonyms = anime["alternative_titles"]?["synonyms"] as JArray;
            if (synonyms != null)
            {
                foreach (var s in synonyms)
                {
                    var v = (string)s;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            return null;
        }

        public async Task<List<AnimeEntry>> GetUserListEntriesAsync(TokenData tokenData)
        {
            if (string.IsNullOrEmpty(tokenData?.access_token)) return [];

            // MAL caps animelist at 1000/page, so a single round-trip covers most users; a
            // power user with thousands of entries finishes in 2-3 pages. The fields request
            // pulls every list_status attribute the sync fan-out needs, plus num_episodes
            // off the node so we can carry TotalEpisodes through.
            var entries = new List<AnimeEntry>();
            int offset = 0;
            while (true)
            {
                var fields = $"node{{id,num_episodes}},{UserListStatusFields}";
                var url = $"{MalApi}/users/@me/animelist?fields={fields}&limit={FullFetchPageSize}&sort=list_updated_at"
                          + (offset > 0 ? $"&offset={offset}" : "");

                var json = await GetJsonAsync(url, tokenData);
                if (json == null) break;
                if (json["data"] is not JArray dataArr || dataArr.Count == 0) break;

                foreach (var raw in dataArr.OfType<JObject>())
                {
                    var node = raw["node"] as JObject;
                    var listStatus = raw["list_status"] as JObject;
                    if (node == null || listStatus == null) continue;

                    var malId = (string)node["id"]?.ToString();
                    if (string.IsNullOrEmpty(malId)) continue;

                    var rawStatus = (string)listStatus["status"];
                    var isRewatching = (bool?)listStatus["is_rewatching"] ?? false;

                    entries.Add(new AnimeEntry
                    {
                        // No EntryId: MAL keys list-status by anime id, and the sync flow
                        // re-derives that from the prefixed MediaId on save.
                        // Prefix so the sync orchestrator hands it straight to GetIdByService.
                        MediaId = $"{malPrefix}{malId}",
                        // Surface "rewatching" as a synthetic value the sync flow recognises —
                        // mirrors GetAnimeEntryAsync above.
                        Status = isRewatching ? "rewatching" : rawStatus,
                        Progress = (int?)listStatus["num_episodes_watched"] ?? 0,
                        TotalEpisodes = (int?)node["num_episodes"],
                        Score = NullableScore((double?)listStatus["score"]),
                        Notes = (string)listStatus["comments"],
                        RewatchCount = (int?)listStatus["num_times_rewatched"] ?? 0,
                        StartedAt = ParseProviderDate((string)listStatus["start_date"]),
                        FinishedAt = ParseProviderDate((string)listStatus["finish_date"]),
                    });
                }

                if (dataArr.Count < FullFetchPageSize) break;
                offset += FullFetchPageSize;
            }

            return entries;
        }

        /// <summary>
        /// Pulls the YouTube video id out of MAL's <c>videos</c> array. The field is sparsely
        /// populated and MAL stores full URLs (not bare ids), so we parse the first watch URL we
        /// find. Returns null when nothing usable is in the response.
        /// </summary>
        private static string ExtractMalYoutubeTrailerId(JObject json)
        {
            if (json["videos"] is not JArray videos) return null;
            foreach (var v in videos.OfType<JObject>())
            {
                var url = (string)v["url"];
                if (string.IsNullOrEmpty(url)) continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

                // youtu.be/<id> — id is the path segment.
                if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                {
                    var segment = uri.AbsolutePath.Trim('/');
                    if (!string.IsNullOrEmpty(segment)) return segment;
                    continue;
                }

                // youtube.com/watch?v=<id> — pull from the query string. We avoid
                // System.Web.HttpUtility so the project doesn't gain a new dependency.
                if (!uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)) continue;
                var query = uri.Query.TrimStart('?');
                foreach (var pair in query.Split('&'))
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(pair[..eq], "v", StringComparison.OrdinalIgnoreCase)) continue;
                    var id = Uri.UnescapeDataString(pair[(eq + 1)..]);
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            return null;
        }
    }
}
