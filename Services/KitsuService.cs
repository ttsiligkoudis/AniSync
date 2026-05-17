using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.Text;

namespace AnimeList.Services
{
    public class KitsuService : IKitsuService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly IAnilistFallback _anilistFallback;
        private readonly ICinemetaService _cinemetaService;
        private readonly ILogger<KitsuService> _logger;
        private readonly string _kitsuApi = "https://kitsu.io/api/edge";
        private static readonly HashSet<ListType> _userLists =
        [
            ListType.Current, ListType.Completed,
            ListType.Planning, ListType.Paused, ListType.Dropped,
            // Kitsu has no "Repeating" status; it's intentionally excluded.
        ];

        // Kitsu enforces a maximum of 20 items per page
        private const int CatalogPageSize = 20;

        public KitsuService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService, IAnilistFallback anilistFallback, ICinemetaService cinemetaService, ILogger<KitsuService> logger)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _anilistFallback = anilistFallback;
            _cinemetaService = cinemetaService;
            _logger = logger;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null, bool groupSeasons = true, string season = null, bool hideUnreleased = false)
        {
            // Kitsu has no native airing-schedule endpoint; delegate to the AniList fallback
            // and translate ids back to Kitsu for downstream meta/manage flows. genre
            // threads through so Airing-by-genre swaps to the RELEASING+genre query.
            if (list == ListType.Airing)
                return await _anilistFallback.GetAiringScheduleAsync(AnimeService.Kitsu, skip, genre);

            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu);
            var isUserList = !list.HasValue || _userLists.Contains(list.Value);

            // User-list endpoints require authentication
            if (isUserList && string.IsNullOrEmpty(tokenData?.user_id))
                return [];

            // For "browse my list" with grouping on we walk every page server-side, dedup
            // across all of them, and UserListCache caches the deduped result. With grouping
            // off the manifest declares the `skip` extra so Stremio paginates one Kitsu page
            // at a time — every entry already has a unique native id, so per-page dedup
            // doesn't shrink the response and the "short page = no more" Stremio heuristic
            // that bites the dedup path doesn't apply.
            var fetchAll = isUserList && groupSeasons && string.IsNullOrEmpty(resolvedAnimeId);

            await _mappingService.EnsureLoadedAsync();
            var seenIds = new Dictionary<string, Meta>();

            // First page is sequential — for the fetch-all path we need its meta.count to
            // fan out the remaining pages in parallel.
            var firstSkip = fetchAll ? "0" : skip;
            var firstUrl = BuildListUrl(tokenData, list, firstSkip, resolvedAnimeId, genre, search, sort, isUserList, season);
            var firstPage = await FetchKitsuPageAsync(firstUrl, tokenData);
            if (firstPage == null) return [];

            await ProcessKitsuPageAsync(firstPage, list, isUserList, genre, seenIds, groupSeasons, hideUnreleased);

            if (fetchAll)
            {
                // Kitsu caps page[limit] at 20, so a 1000-entry library would otherwise need
                // 50 serial round-trips (~7-10s on a cold view). Use meta.count to know the
                // total upfront and fire the remaining pages concurrently. SemaphoreSlim caps
                // the in-flight count so a power user can't hammer Kitsu's rate limit.
                var totalCount = SafeGet<int?>(firstPage, "meta", "count");
                var firstDataCount = (firstPage["data"] as JArray)?.Count ?? 0;

                if (totalCount.HasValue && totalCount.Value > CatalogPageSize)
                {
                    using var sem = new SemaphoreSlim(8);
                    var tasks = new List<Task<JObject>>();
                    for (int offset = CatalogPageSize; offset < totalCount.Value; offset += CatalogPageSize)
                    {
                        var url = BuildListUrl(tokenData, list, offset.ToString(), resolvedAnimeId, genre, search, sort, isUserList, season);
                        tasks.Add(FetchWithSemaphoreAsync(url, tokenData, sem));
                    }

                    var pages = await Task.WhenAll(tasks);
                    foreach (var page in pages)
                    {
                        if (page == null) continue;
                        await ProcessKitsuPageAsync(page, list, isUserList, genre, seenIds, groupSeasons, hideUnreleased);
                    }
                }
                else if (!totalCount.HasValue && firstDataCount >= CatalogPageSize)
                {
                    // Fallback: no meta.count (shouldn't happen on Kitsu but defend against
                    // it anyway) — walk pages serially until we hit one shorter than the
                    // page size, the same way the rest of the codebase did before.
                    int offset = CatalogPageSize;
                    while (true)
                    {
                        var url = BuildListUrl(tokenData, list, offset.ToString(), resolvedAnimeId, genre, search, sort, isUserList, season);
                        var page = await FetchKitsuPageAsync(url, tokenData);
                        if (page == null) break;

                        var dataCount = (page["data"] as JArray)?.Count ?? 0;
                        if (dataCount == 0) break;

                        await ProcessKitsuPageAsync(page, list, isUserList, genre, seenIds, groupSeasons, hideUnreleased);
                        if (dataCount < CatalogPageSize) break;
                        offset += CatalogPageSize;
                    }
                }
            }

            // Sort user libraries alphabetically by name so franchise cours sit next to each
            // other ("Show", "Show Part 2", "Show Season 2", …) — only meaningful when we
            // have the whole library in memory (grouping on). With grouping off we return
            // a single upstream page so the sort would only reorder within that window;
            // keep Kitsu's own progressed_at ordering instead. Discovery catalogs always
            // keep their API ranking.
            if (isUserList && fetchAll)
                return seenIds.Values
                    .OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            return seenIds.Values.ToList();
        }

        private async Task<JObject> FetchKitsuPageAsync(string url, TokenData tokenData)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyBearerAuth(request, tokenData);

            var response = await _clientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            return JObject.Parse(content);
        }

        private async Task<JObject> FetchWithSemaphoreAsync(string url, TokenData tokenData, SemaphoreSlim sem)
        {
            await sem.WaitAsync();
            try { return await FetchKitsuPageAsync(url, tokenData); }
            finally { sem.Release(); }
        }

        // Parses a single Kitsu list-style page and merges the entries into <paramref name="seenIds"/>.
        // Centralised so the parallel-fan-out path and the fallback serial path share the same
        // dedup, genre-filter, and mapping-resolution logic.
        private async Task ProcessKitsuPageAsync(JObject json, ListType? list, bool isUserList, string genre, Dictionary<string, Meta> seenIds, bool groupSeasons, bool hideUnreleased)
        {
            // Build O(1) lookups for `included` resources by id, separated by type
            var includedAnime = new Dictionary<string, JObject>();
            var categoryNames = new Dictionary<string, string>();
            if (json["included"] is JArray includedArr)
            {
                foreach (var inc in includedArr.OfType<JObject>())
                {
                    var incType = (string)inc["type"];
                    var incId = (string)inc["id"];
                    if (incId == null) continue;

                    if (incType == "anime") includedAnime[incId] = inc;
                    else if (incType == "categories")
                    {
                        var title = SafeGet<string>(inc, "attributes", "title");
                        if (!string.IsNullOrEmpty(title)) categoryNames[incId] = title;
                    }
                }
            }

            if (json["data"] is not JArray dataArr) return;

            foreach (var entry in dataArr.OfType<JObject>())
            {
                JObject anime;
                string entryId = null;
                string entryStatus = null;

                int? entryProgress = null;
                if (isUserList)
                {
                    var animeRefId = SafeGet<string>(entry, "relationships", "anime", "data", "id");
                    if (string.IsNullOrEmpty(animeRefId) || !includedAnime.TryGetValue(animeRefId, out anime)) continue;
                    entryId = (string)entry["id"];
                    entryStatus = SafeGet<string>(entry, "attributes", "status");
                    // attributes.progress is the user's watched-episode count on
                    // Kitsu library-entries — same shape as AniList's entry.progress.
                    entryProgress = SafeGet<int?>(entry, "attributes", "progress");
                }
                else
                {
                    anime = entry;
                }

                var status = SafeGet<string>(anime, "attributes", "status");
                if (hideUnreleased && list == ListType.Current && status is "tba" or "unreleased" or "upcoming") continue;

                var animeKitsuId = (string)anime["id"];

                // Resolve category names from the included array
                var animeGenres = ExtractCategories(anime, categoryNames);

                // Filter user list entries by genre when discover-only provides a genre selection
                if (!string.IsNullOrEmpty(genre) && isUserList)
                {
                    if (animeGenres == null || !animeGenres.Any(g => string.Equals(g, genre, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                var mapping = await _mappingService.GetKitsuMapping(animeKitsuId);

                // groupSeasons=true → fall through to IMDb / TMDB before native so multiple
                // cours of a franchise collapse to one card via the dedup step below. When the
                // user disables grouping, every cour gets its own native id and dedup is a
                // no-op since native ids don't collide.
                var (externalId, _, _) = ResolveGroupedId(
                    mapping, $"{kitsuPrefix}{animeKitsuId}", groupSeasons, allowKitsuFallback: false);

                var subtype = SafeGet<string>(anime, "attributes", "subtype");
                var isMovie = IsMovieFormat(subtype);

                // StreamD-style card chrome: score + episodes + year + format. Kitsu's
                // averageRating is a 0-100 string (e.g. "78.43"), so parse + scale to
                // 0-10 with one decimal to match the cross-provider format. startDate
                // is "YYYY-MM-DD" so we slice the year off the front rather than
                // parsing through DateTime.
                double? scoreParsed = null;
                var ratingStr = SafeGet<string>(anime, "attributes", "averageRating");
                if (!string.IsNullOrEmpty(ratingStr) &&
                    double.TryParse(ratingStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ratingNum))
                    scoreParsed = Math.Round(ratingNum / 10, 1);

                int? releaseYear = null;
                var startDateStr = SafeGet<string>(anime, "attributes", "startDate");
                if (!string.IsNullOrEmpty(startDateStr) && startDateStr.Length >= 4 &&
                    int.TryParse(startDateStr[..4], out var y))
                    releaseYear = y;

                var episodeCount = SafeGet<int?>(anime, "attributes", "episodeCount");

                var meta = new Meta(SafeGet<string>(anime, "attributes", "description"))
                {
                    id = externalId,
                    type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = ExtractTitle(anime),
                    poster = SafeGet<string>(anime, "attributes", "posterImage", "large"),
                    entryId = entryId,
                    entryStatus = entryStatus,
                    score = scoreParsed,
                    episodes = episodeCount > 0 ? episodeCount : null,
                    year = releaseYear,
                    format = NormalizeFormat(subtype),
                    progress = entryProgress,
                };

                // Multiple Kitsu entries (seasons/OVAs) can share the same IMDb ID;
                // keep the shortest English title as it's typically the base series name
                if (seenIds.TryGetValue(externalId, out var existing))
                {
                    if (!string.IsNullOrEmpty(meta.name) && (string.IsNullOrEmpty(existing.name) || meta.name.Length < existing.name.Length))
                        seenIds[externalId] = meta;
                    continue;
                }

                seenIds[externalId] = meta;
            }
        }

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData, bool groupSeasons = true)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(id, AnimeService.Kitsu);
            if (string.IsNullOrEmpty(resolvedAnimeId)) return null;

            // animeProductions.producer pulls each studio/producer relationship and the
            // related producer node (name + slug) so we can surface Studio links the same
            // way AniList and MAL do.
            var url = $"{_kitsuApi}/anime/{resolvedAnimeId}?include=categories,episodes,animeProductions.producer";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyBearerAuth(request, tokenData);

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            if (json["data"] is not JObject entry) return null;

            var mapping = await _mappingService.GetKitsuMapping(resolvedAnimeId);

            // Same toggle as the catalog path: when grouping is on, prefer cross-service ids
            // so meta.id matches what the user clicked from a grouped catalog. When off, keep
            // the response in this service's native id space.
            // videoId will still use the groupId since it is better source for streams.
            var (externalId, groupId, hasGroupId) = ResolveGroupedId(
                mapping, $"{kitsuPrefix}{(string)entry["id"]}", groupSeasons, allowKitsuFallback: false);

            var subtype = SafeGet<string>(entry, "attributes", "subtype");
            var isMovie = IsMovieFormat(subtype);

            // Pull categories, episodes, animeProductions and producers out of the included array
            var categoryTitles = new List<string>();
            var episodes = new List<JObject>();
            var animeProductions = new List<JObject>();
            // producer-id → (name, slug) so we can resolve animeProduction → producer in a
            // second pass without re-walking included.
            var producers = new Dictionary<string, (string Name, string Slug)>();
            if (json["included"] is JArray includedArr)
            {
                foreach (var inc in includedArr.OfType<JObject>())
                {
                    var incType = (string)inc["type"];
                    if (incType == "categories")
                    {
                        var catTitle = SafeGet<string>(inc, "attributes", "title");
                        if (!string.IsNullOrEmpty(catTitle)) categoryTitles.Add(catTitle);
                    }
                    else if (incType == "episodes")
                    {
                        episodes.Add(inc);
                    }
                    else if (incType == "animeProductions")
                    {
                        animeProductions.Add(inc);
                    }
                    else if (incType == "producers")
                    {
                        var pid = (string)inc["id"];
                        var pname = SafeGet<string>(inc, "attributes", "name");
                        var pslug = SafeGet<string>(inc, "attributes", "slug");
                        if (!string.IsNullOrEmpty(pid) && !string.IsNullOrEmpty(pname))
                            producers[pid] = (pname, pslug);
                    }
                }
            }

            // Mirror the catalog Meta builder so /anime/{id} renders consistent
            // hero chrome — score badge, "TV · 13 eps · 2026" info row.
            // averageRating is a 0-100 string, scaled to 0-10. startDate is
            // "YYYY-MM-DD" so the leading 4 chars are the year. episodeCount
            // and subtype are direct attribute reads.
            double? scoreParsed = null;
            var ratingStr = SafeGet<string>(entry, "attributes", "averageRating");
            if (!string.IsNullOrEmpty(ratingStr) &&
                double.TryParse(ratingStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ratingNum))
                scoreParsed = Math.Round(ratingNum / 10, 1);

            int? releaseYear = null;
            var startDateStr = SafeGet<string>(entry, "attributes", "startDate");
            if (!string.IsNullOrEmpty(startDateStr) && startDateStr.Length >= 4 &&
                int.TryParse(startDateStr[..4], out var y))
                releaseYear = y;

            var episodeCount = SafeGet<int?>(entry, "attributes", "episodeCount");

            var anime = new Meta(SafeGet<string>(entry, "attributes", "description"))
            {
                id = externalId,
                type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = ExtractTitle(entry),
                poster = SafeGet<string>(entry, "attributes", "posterImage", "large"),
                background = SafeGet<string>(entry, "attributes", "coverImage", "original")
                             ?? SafeGet<string>(entry, "attributes", "coverImage", "large")
                             ?? SafeGet<string>(entry, "attributes", "posterImage", "original"),
                genres = categoryTitles.Count > 0 ? categoryTitles : null,
                score = scoreParsed,
                episodes = episodeCount > 0 ? episodeCount : null,
                year = releaseYear,
                format = NormalizeFormat(subtype),
                // Kitsu surfaces "status" via attributes (current / finished /
                // tba / unreleased / upcoming). No equivalent of AniList's
                // `source` enum on Kitsu so source stays null — the view's
                // info row gracefully omits.
                airStatus = NormalizeAirStatus(SafeGet<string>(entry, "attributes", "status")),
            };

            var youtubeId = SafeGet<string>(entry, "attributes", "youtubeVideoId");
            if (string.IsNullOrEmpty(youtubeId) && mapping?.AnilistId != null)
            {
                // Kitsu's youtubeVideoId is sparsely populated — fall back to AniList through
                // the cross-service mapping when we can. Mirrors the MAL service's behaviour.
                try { youtubeId = await _anilistFallback.GetYoutubeTrailerIdAsync(mapping.AnilistId.Value); }
                catch { /* best-effort enrichment */ }
            }
            if (!string.IsNullOrEmpty(youtubeId))
            {
                anime.trailers.Add(new Trailer(youtubeId));
                anime.trailerStreams.Add(new TrailerStream(youtubeId, anime.name));
            }

            // Studios: animeProductions are joined to producer nodes. Filter to role=studio
            // (the actual animation studio); other roles are producers/licensors which
            // AniList's `isMain=true` heuristic intentionally drops too.
            foreach (var prod in animeProductions)
            {
                if (SafeGet<string>(prod, "attributes", "role") != "studio") continue;
                var producerId = SafeGet<string>(prod, "relationships", "producer", "data", "id");
                if (string.IsNullOrEmpty(producerId) || !producers.TryGetValue(producerId, out var producer))
                    continue;
                anime.links.Add(new Link
                {
                    name = producer.Name,
                    category = "Studio",
                    // Kitsu doesn't expose public producer pages (slug is internal-only); leave
                    // url null rather than minting a 404. Stremio shows the name regardless.
                    url = null,
                });
            }

            if (!isMovie)
            {
                // Prefer Cinemeta when we have an IMDb mapping — its per-episode coverage
                // (titles, thumbs, synopses, air dates) is dramatically richer than Kitsu's
                // volunteer-maintained episode catalog. Falls through to the local episode
                // include when there's no IMDb mapping or Cinemeta returns nothing. Wrapped
                // in try/catch because a malformed mapping (Season parser, etc.) shouldn't
                // take the whole meta page down.
                if (!string.IsNullOrEmpty(mapping?.ImdbId))
                {
                    try
                    {
                        var kitsuIdInt = int.TryParse(resolvedAnimeId, out var kid) ? kid : 0;
                        var currentEpisodeCount = SafeGet<int?>(entry, "attributes", "episodeCount") ?? 0;
                        anime.videos = await _cinemetaService.GetCourEpisodesAsync(
                            mapping.ImdbId, mapping.Season, AnimeService.Kitsu,
                            kitsuIdInt, currentEpisodeCount, GetAnimeSummaryAsync);
                    }
                    catch
                    {
                        anime.videos = [];
                    }
                }

                if (anime.videos.Count == 0)
                {
                    var sortedEpisodes = episodes
                        .OrderBy(e => SafeGet<int?>(e, "attributes", "seasonNumber") ?? 1)
                        .ThenBy(e => SafeGet<int?>(e, "attributes", "number") ?? 0)
                        .ToList();

                    int episodeNumber = 1;
                    foreach (var episode in sortedEpisodes)
                    {
                        var seasonNumber = SafeGet<int?>(episode, "attributes", "seasonNumber") ?? 1;
                        if (seasonNumber <= 0) seasonNumber = 1;
                        var thumbnail = SafeGet<string>(episode, "attributes", "thumbnail", "original")
                                        ?? SafeGet<string>(episode, "attributes", "thumbnail", "large");
                        var epTitle = SafeGet<string>(episode, "attributes", "canonicalTitle");

                        anime.videos.Add(new Video
                        {
                            id = hasGroupId ? $"{groupId}:{seasonNumber}:{episodeNumber}" : $"{groupId}:{episodeNumber}",
                            title = string.IsNullOrEmpty(epTitle) ? $"Episode {episodeNumber}" : epTitle,
                            thumbnail = thumbnail,
                            season = seasonNumber,
                            episode = episodeNumber,
                        });
                        episodeNumber++;
                    }
                }

                // Stremio rejects (renders blank) when video.id doesn't share a prefix with
                // meta.id, so make sure every video lives in the calling service's id space
                // regardless of whether the loop above ran or Cinemeta filled them in.
                NormalizeVideoIds(anime.videos, groupId, hasGroupId);
            }

            // Kitsu's mediaRelationships exposes prequels/sequels but not "audience also liked"
            // recommendations. Fall back to AniList anonymously when the anime has an AniList id
            // in the mapping; the fallback rewrites ids back to Kitsu where possible. Wrapped
            // in try/catch so a transient AniList failure can't take the whole meta down with it.
            if (mapping?.AnilistId != null)
            {
                try
                {
                    var similar = await _anilistFallback.GetRecommendationsAsync(mapping.AnilistId.Value, AnimeService.Kitsu);
                    anime.links.AddRange(similar);
                    // Parallel call for the detail-page carousel — same upstream,
                    // richer per-rec shape. Kept separate from the Link-flavoured
                    // fallback above because the addon JSON path consumes Links
                    // and the web app's carousel consumes Metas. Best-effort.
                    // translateTo: Kitsu — recommendation cards under a
                    // Kitsu-primary detail page should hand off to /anime/kitsu:N
                    // when the mapping has a kitsu id, anilist:N otherwise.
                    var recMetas = await _anilistFallback.GetRecommendationMetasAsync(mapping.AnilistId.Value, AnimeService.Kitsu);
                    anime.recommendations.AddRange(recMetas);
                }
                catch
                {
                    // best-effort enrichment
                }
            }

            // links must be valid or stremio throws error and page can't render. 
            anime.links = anime.links.Where(w => IsValidUrl(w.url)).ToList();

            return anime;
        }

        public async Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu, season);
            if (string.IsNullOrEmpty(resolvedKitsuId)) return null;
            return await GetAnimeEntryByResolvedIdAsync(tokenData, resolvedKitsuId);
        }

        private async Task<AnimeEntry> GetAnimeEntryByResolvedIdAsync(TokenData tokenData, string resolvedKitsuId)
        {
            var entry = new AnimeEntry { MediaId = resolvedKitsuId };

            // Without auth we still want totalEpisodes so the UI can show a progress max
            if (string.IsNullOrEmpty(tokenData?.access_token) || string.IsNullOrEmpty(tokenData?.user_id))
            {
                entry.TotalEpisodes = await GetTotalEpisodesAsync(tokenData, resolvedKitsuId);
                return entry;
            }

            // One round-trip: library entry + the anime's episodeCount via sparse fields on the include
            var url = $"{_kitsuApi}/users/{tokenData.user_id}/library-entries"
                + $"?filter[kind]=anime&filter[animeId]={resolvedKitsuId}&page[limit]=1"
                + "&include=anime&fields[anime]=episodeCount";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyBearerAuth(request, tokenData);

            var response = await _clientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                entry.TotalEpisodes = await GetTotalEpisodesAsync(tokenData, resolvedKitsuId);
                return entry;
            }

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            if ((json["data"] as JArray)?.OfType<JObject>().FirstOrDefault() is JObject libEntry)
            {
                var attrs = libEntry["attributes"] as JObject;
                entry.EntryId = (string)libEntry["id"];
                entry.Status = (string)attrs?["status"];
                entry.Progress = (int?)attrs?["progress"] ?? 0;
                // Kitsu's ratingTwenty is 2-20 (where 20 == 10/10); convert to a 0-10 scale.
                var ratingTwenty = (int?)attrs?["ratingTwenty"];
                entry.Score = ratingTwenty.HasValue ? ratingTwenty.Value / 2.0 : null;
                entry.Notes = (string)attrs?["notes"];
                entry.RewatchCount = (int?)attrs?["reconsumeCount"] ?? 0;
                entry.StartedAt = ParseProviderDate((string)attrs?["startedAt"]);
                entry.FinishedAt = ParseProviderDate((string)attrs?["finishedAt"]);
            }

            entry.TotalEpisodes = (int?)((json["included"] as JArray)?
                .OfType<JObject>()
                .FirstOrDefault(i => (string)i["type"] == "anime") is JObject animeInc
                    ? SafeGet<int?>(animeInc, "attributes", "episodeCount")
                    : null)
                ?? await GetTotalEpisodesAsync(tokenData, resolvedKitsuId);

            return entry;
        }

        public async Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu, season);
            if (string.IsNullOrEmpty(resolvedKitsuId))
            {
                _logger.LogWarning("Kitsu save skipped — no Kitsu mapping for animeId={AnimeId} season={Season}.", animeId, season);
                return;
            }
            if (string.IsNullOrEmpty(tokenData?.access_token) || string.IsNullOrEmpty(tokenData?.user_id))
            {
                _logger.LogWarning("Kitsu save skipped — token has no access_token/user_id (id={ResolvedKitsuId}).", resolvedKitsuId);
                return;
            }

            var existing = await GetAnimeEntryByResolvedIdAsync(tokenData, resolvedKitsuId);

            if (string.IsNullOrEmpty(status)) status = existing?.Status;
            // Kitsu requires a status when creating; default new entries to current
            if (string.IsNullOrEmpty(status)) status = GetListTypeString(ListType.Current, tokenData);

            // Build a sparse attributes dict so unset fields aren't overwritten with null
            var attributes = new Dictionary<string, object>
            {
                ["progress"] = progress,
                ["status"] = status,
            };
            // Kitsu's ratingTwenty has a hard minimum of 2 (anything 0 or 1 is rejected
            // with a 422). Skip the field entirely when the caller's score doesn't clear
            // that floor — better to leave the existing rating untouched than to fail the
            // whole save because of a "no rating" sentinel from a sister service.
            if (score.HasValue && score.Value > 0)
            {
                var ratingTwenty = (int)Math.Round(Math.Clamp(score.Value, 0, 10) * 2);
                if (ratingTwenty >= 2) attributes["ratingTwenty"] = ratingTwenty;
            }
            if (notes != null) attributes["notes"] = notes;
            if (rewatchCount.HasValue) attributes["reconsumeCount"] = rewatchCount.Value;
            if (startedAt.HasValue) attributes["startedAt"] = startedAt.Value.ToString("yyyy-MM-dd");
            if (finishedAt.HasValue) attributes["finishedAt"] = finishedAt.Value.ToString("yyyy-MM-dd");

            HttpRequestMessage request;
            if (!string.IsNullOrEmpty(existing?.EntryId))
            {
                var body = new
                {
                    data = new
                    {
                        type = "libraryEntries",
                        id = existing.EntryId,
                        attributes,
                    }
                };

                request = new HttpRequestMessage(HttpMethod.Patch, $"{_kitsuApi}/library-entries/{existing.EntryId}")
                {
                    Content = new StringContent(SerializeObject(body), Encoding.UTF8, "application/vnd.api+json")
                };
            }
            else
            {
                var body = new
                {
                    data = new
                    {
                        type = "libraryEntries",
                        attributes,
                        relationships = new
                        {
                            user = new { data = new { type = "users", id = tokenData.user_id } },
                            anime = new { data = new { type = "anime", id = resolvedKitsuId } }
                        }
                    }
                };

                request = new HttpRequestMessage(HttpMethod.Post, $"{_kitsuApi}/library-entries")
                {
                    Content = new StringContent(SerializeObject(body), Encoding.UTF8, "application/vnd.api+json")
                };
            }

            ApplyBearerAuth(request, tokenData);
            _logger.LogInformation("Kitsu {Method} library-entries id={ResolvedKitsuId} status={Status} progress={Progress}.",
                existing?.EntryId != null ? "PATCH" : "POST", resolvedKitsuId, status, progress);
            var saveResponse = await _clientFactory.CreateClient().SendAsync(request);
            await EnsureSuccessOrThrow(saveResponse, "Kitsu", "save", includeBody: true);
        }

        public async Task<(string? name, int? episodeCount)> GetAnimeSummaryAsync(string id)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(id, AnimeService.Kitsu);
            if (string.IsNullOrEmpty(resolvedKitsuId)) return (null, null);

            // Sparse fieldset — the dropdown only needs title and episodeCount, not the
            // ~20 KB payload that the full meta query returns (categories, episodes
            // include, etc.).
            var url = $"{_kitsuApi}/anime/{resolvedKitsuId}?fields[anime]=titles,canonicalTitle,episodeCount";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            var response = await _clientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode) return (null, null);

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            if (json["data"] is not JObject entry) return (null, null);

            var name = ExtractTitle(entry);
            var episodeCount = SafeGet<int?>(entry, "attributes", "episodeCount");
            return (name, episodeCount);
        }

        public async Task<List<StreamingLink>> GetExternalLinksAsync(string animeId, TokenData tokenData)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu);
            if (string.IsNullOrEmpty(resolvedKitsuId)) return [];

            var url = $"{_kitsuApi}/anime/{resolvedKitsuId}?include=streamingLinks.streamer&fields[anime]=id&fields[streamingLinks]=url,streamer&fields[streamers]=siteName";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyBearerAuth(request, tokenData);

            var response = await _clientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            // Build streamer-id -> siteName map first
            var streamers = new Dictionary<string, string>();
            if (json["included"] is JArray includedArr)
            {
                foreach (var inc in includedArr.OfType<JObject>())
                {
                    if ((string)inc["type"] != "streamers") continue;
                    var sid = (string)inc["id"];
                    var name = SafeGet<string>(inc, "attributes", "siteName");
                    if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(name)) streamers[sid] = name;
                }
            }

            var result = new List<StreamingLink>();
            if (json["included"] is JArray included2)
            {
                foreach (var inc in included2.OfType<JObject>())
                {
                    if ((string)inc["type"] != "streamingLinks") continue;
                    var linkUrl = SafeGet<string>(inc, "attributes", "url");
                    if (string.IsNullOrEmpty(linkUrl)) continue;

                    var streamerId = SafeGet<string>(inc, "relationships", "streamer", "data", "id");
                    var siteName = streamerId != null && streamers.TryGetValue(streamerId, out var n) ? n : "Stream";
                    result.Add(new StreamingLink { Site = siteName, Url = linkUrl });
                }
            }
            return result;
        }

        public async Task DeleteAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null)
        {
            if (string.IsNullOrWhiteSpace(tokenData?.access_token) || string.IsNullOrEmpty(tokenData?.user_id))
                return;

            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu, season);
            if (string.IsNullOrEmpty(resolvedKitsuId)) return;

            // Need the library-entry id, not the anime id, for the DELETE. Fetch via the
            // user's list; if the entry doesn't exist there's nothing to remove.
            var existing = await GetAnimeEntryByResolvedIdAsync(tokenData, resolvedKitsuId);
            if (string.IsNullOrEmpty(existing?.EntryId)) return;

            var request = new HttpRequestMessage(HttpMethod.Delete, $"{_kitsuApi}/library-entries/{existing.EntryId}");
            ApplyBearerAuth(request, tokenData);

            var deleteResponse = await _clientFactory.CreateClient().SendAsync(request);
            await EnsureSuccessOrThrow(deleteResponse, "Kitsu", "delete", includeBody: true);
        }

        public async Task<List<AnimeEntry>> GetUserListEntriesAsync(TokenData tokenData)
        {
            if (string.IsNullOrEmpty(tokenData?.access_token) || string.IsNullOrEmpty(tokenData.user_id))
                return [];

            // Use the same parallel-fan-out pattern as the catalog list fetch: read meta.count
            // off the first page, then fire the rest concurrently behind a SemaphoreSlim. Sparse
            // fields keep each page small — we only need the list_status attributes plus the
            // related anime id for prefixing on the way back out.
            var firstUrl = BuildLibraryUrl(tokenData.user_id, offset: 0);
            var firstPage = await FetchKitsuPageAsync(firstUrl, tokenData);
            if (firstPage == null) return [];

            var totalCount = SafeGet<int?>(firstPage, "meta", "count");
            var firstDataCount = (firstPage["data"] as JArray)?.Count ?? 0;

            var entries = new List<AnimeEntry>();
            CollectKitsuEntries(firstPage, entries);

            if (totalCount.HasValue && totalCount.Value > CatalogPageSize)
            {
                using var sem = new SemaphoreSlim(8);
                var tasks = new List<Task<JObject>>();
                for (int offset = CatalogPageSize; offset < totalCount.Value; offset += CatalogPageSize)
                {
                    var url = BuildLibraryUrl(tokenData.user_id, offset);
                    tasks.Add(FetchWithSemaphoreAsync(url, tokenData, sem));
                }
                var pages = await Task.WhenAll(tasks);
                foreach (var page in pages)
                    if (page != null) CollectKitsuEntries(page, entries);
            }
            else if (!totalCount.HasValue && firstDataCount >= CatalogPageSize)
            {
                // Defensive: walk pages serially if Kitsu ever stops shipping meta.count.
                int offset = CatalogPageSize;
                while (true)
                {
                    var url = BuildLibraryUrl(tokenData.user_id, offset);
                    var page = await FetchKitsuPageAsync(url, tokenData);
                    if (page == null) break;
                    var dataCount = (page["data"] as JArray)?.Count ?? 0;
                    if (dataCount == 0) break;
                    CollectKitsuEntries(page, entries);
                    if (dataCount < CatalogPageSize) break;
                    offset += CatalogPageSize;
                }
            }

            return entries;
        }

        private string BuildLibraryUrl(string userId, int offset) =>
            $"{_kitsuApi}/users/{userId}/library-entries"
            + "?filter[kind]=anime"
            + "&include=anime"
            + "&fields[anime]=episodeCount"
            + "&fields[libraryEntries]=status,progress,ratingTwenty,reconsumeCount,startedAt,finishedAt,notes,anime"
            + $"&page[limit]={CatalogPageSize}"
            + (offset > 0 ? $"&page[offset]={offset}" : "");

        private static void CollectKitsuEntries(JObject json, List<AnimeEntry> entries)
        {
            // Build a sparse anime-id → episodeCount map from the include payload so each
            // entry can carry its TotalEpisodes through the sync without an extra round trip.
            var animeEpisodes = new Dictionary<string, int?>();
            if (json["included"] is JArray includedArr)
            {
                foreach (var inc in includedArr.OfType<JObject>())
                {
                    if ((string)inc["type"] != "anime") continue;
                    var id = (string)inc["id"];
                    if (string.IsNullOrEmpty(id)) continue;
                    animeEpisodes[id] = SafeGet<int?>(inc, "attributes", "episodeCount");
                }
            }

            if (json["data"] is not JArray dataArr) return;

            foreach (var libEntry in dataArr.OfType<JObject>())
            {
                var animeRefId = SafeGet<string>(libEntry, "relationships", "anime", "data", "id");
                if (string.IsNullOrEmpty(animeRefId)) continue;

                var attrs = libEntry["attributes"] as JObject;
                var ratingTwenty = (int?)attrs?["ratingTwenty"];

                entries.Add(new AnimeEntry
                {
                    EntryId = (string)libEntry["id"],
                    // Prefix so the sync orchestrator can hand this straight to GetIdByService.
                    MediaId = $"{kitsuPrefix}{animeRefId}",
                    Status = (string)attrs?["status"],
                    Progress = (int?)attrs?["progress"] ?? 0,
                    TotalEpisodes = animeEpisodes.GetValueOrDefault(animeRefId),
                    // Translate Kitsu's ratingTwenty (2–20) back to the shared 0–10 scale,
                    // dropping the "no score" sentinel (null or 0) on the way.
                    Score = (ratingTwenty.HasValue && ratingTwenty.Value > 0) ? ratingTwenty.Value / 2.0 : null,
                    Notes = (string)attrs?["notes"],
                    RewatchCount = (int?)attrs?["reconsumeCount"] ?? 0,
                    StartedAt = ParseProviderDate((string)attrs?["startedAt"]),
                    FinishedAt = ParseProviderDate((string)attrs?["finishedAt"]),
                });
            }
        }

        private async Task<int?> GetTotalEpisodesAsync(TokenData tokenData, string resolvedKitsuId)
        {
            var url = $"{_kitsuApi}/anime/{resolvedKitsuId}?fields[anime]=episodeCount";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyBearerAuth(request, tokenData);

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            return SafeGet<int?>(json, "data", "attributes", "episodeCount");
        }

        private string BuildListUrl(TokenData tokenData, ListType? list, string skip, string resolvedAnimeId, string genre, string search, string sort, bool isUserList, string season = null)
        {
            string url;

            if (isUserList)
            {
                var statusFilter = list.HasValue ? $"&filter[status]={GetListTypeString(list.Value, tokenData)}" : "";
                var animeFilter = !string.IsNullOrEmpty(resolvedAnimeId) ? $"&filter[animeId]={resolvedAnimeId}" : "";
                url = $"{_kitsuApi}/users/{tokenData.user_id}/library-entries?filter[kind]=anime&include=anime,anime.categories{statusFilter}{animeFilter}";
            }
            else if (list == ListType.Seasonal)
            {
                // Explicit `season` ("Spring 2026") wins; otherwise the
                // Stremio addon's `genre`-as-season legacy ("This Season" /
                // "Next Season" / "Previous Season"); otherwise current.
                // Real genre values layer filter[categories] on top.
                var seasonSelector = !string.IsNullOrEmpty(season)
                    ? season
                    : (!string.IsNullOrEmpty(genre) && SeasonOptions.Contains(genre) ? genre : SeasonCurrent);
                var (resolvedSeason, year) = GetSeasonAndYear(seasonSelector);
                var realGenre = (!string.IsNullOrEmpty(genre) && !SeasonOptions.Contains(genre)) ? genre : null;
                var sortValue = string.IsNullOrEmpty(sort) ? "-userCount" : SortToKitsu(sort);
                url = $"{_kitsuApi}/anime?sort={sortValue}&filter[season]={resolvedSeason.ToLowerInvariant()}&filter[seasonYear]={year}&include=categories";
                if (!string.IsNullOrEmpty(realGenre))
                {
                    url += $"&filter[categories]={Uri.EscapeDataString(realGenre.ToLowerInvariant().Replace(" ", "-"))}";
                }
            }
            else if (list == ListType.Search)
            {
                // Search layers genre + season filters on top of the text
                // search so the form's combined intent ("Naruto in Action
                // this season") is honoured. Kitsu's JSON:API accepts the
                // same filter[categories] / filter[season] / filter[seasonYear]
                // segments the Seasonal branch above uses.
                url = $"{_kitsuApi}/anime?include=categories&filter[text]={Uri.EscapeDataString(search ?? string.Empty)}";
                var hasSearchGenre = !string.IsNullOrEmpty(genre) && !SeasonOptions.Contains(genre);
                if (hasSearchGenre)
                {
                    url += $"&filter[categories]={Uri.EscapeDataString(genre.ToLowerInvariant().Replace(" ", "-"))}";
                }
                if (!string.IsNullOrEmpty(season))
                {
                    var (searchSeason, searchYear) = GetSeasonAndYear(season);
                    url += $"&filter[season]={searchSeason.ToLowerInvariant()}&filter[seasonYear]={searchYear}";
                }
            }
            else
            {
                // Trending and other discovery lists default to popularity rank ascending
                // (rank 1 = most popular). Honour an explicit sort override if the user picked one.
                var sortValue = string.IsNullOrEmpty(sort) ? "popularityRank" : SortToKitsu(sort);
                url = $"{_kitsuApi}/anime?sort={sortValue}&include=categories";
                if (!string.IsNullOrEmpty(genre))
                {
                    url += $"&filter[categories]={Uri.EscapeDataString(genre.ToLowerInvariant().Replace(" ", "-"))}";
                }
            }

            url += $"&page[limit]={CatalogPageSize}";
            if (!string.IsNullOrEmpty(skip))
                url += $"&page[offset]={skip}";

            return url;
        }

        private static List<string> ExtractCategories(JObject anime, Dictionary<string, string> categoryNames)
        {
            if (categoryNames.Count == 0) return null;

            if (SafeGet(anime, "relationships", "categories", "data") is not JArray refs) return null;

            return refs.OfType<JObject>()
                .Select(c => (string)c["id"])
                .Where(id => !string.IsNullOrEmpty(id) && categoryNames.ContainsKey(id))
                .Select(id => categoryNames[id])
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }

        private static string ExtractTitle(JObject anime)
        {
            return SafeGet<string>(anime, "attributes", "titles", "en")
                ?? SafeGet<string>(anime, "attributes", "titles", "en_jp")
                ?? SafeGet<string>(anime, "attributes", "canonicalTitle");
        }
    }
}
