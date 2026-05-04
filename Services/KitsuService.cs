using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace AnimeList.Services
{
    public class KitsuService : IKitsuService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly IAnilistFallback _anilistFallback;
        private readonly string _kitsuApi = "https://kitsu.io/api/edge";
        private static readonly HashSet<ListType> _userLists =
        [
            ListType.Current, ListType.Completed,
            ListType.Planning, ListType.Paused, ListType.Dropped,
            // Kitsu has no "Repeating" status; it's intentionally excluded.
        ];

        // Kitsu enforces a maximum of 20 items per page
        private const int CatalogPageSize = 20;

        // Per-user list cache. Stremio paginates through a catalog by calling the same
        // endpoint with rising `skip` values; without a cache each scroll triggers a
        // full multi-page fetch + dedup. Caching the deduped list lets every page-2+
        // request collapse to an in-memory slice.
        // Key: "{user_id}|{listType}|{genre}". Value: full deduped meta list + expiry.
        private static readonly ConcurrentDictionary<string, (DateTime Expiry, List<Meta> Metas)> _userListCache = new();
        private static readonly TimeSpan UserListCacheTtl = TimeSpan.FromMinutes(2);

        public KitsuService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService, IAnilistFallback anilistFallback)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _anilistFallback = anilistFallback;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null)
        {
            // Kitsu has no native airing-schedule endpoint; delegate to the AniList fallback
            // and translate ids back to Kitsu for downstream meta/manage flows.
            if (list == ListType.Airing)
                return await _anilistFallback.GetAiringScheduleAsync(AnimeService.Kitsu, skip);

            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu);
            var isUserList = !list.HasValue || _userLists.Contains(list.Value);

            // User-list endpoints require authentication
            if (isUserList && string.IsNullOrEmpty(tokenData?.user_id))
                return [];

            // For "browse my list" requests we walk every page server-side, dedup across all
            // of them, and apply the user's `skip` after dedup. Server-side pagination + per-
            // page dedup returns short pages that confuse Stremio's "fetch until empty" loop —
            // some catalog views can end up paging in circles or refetching the same skip
            // indefinitely. Mirrors AnilistService's MediaListCollection approach.
            var fetchAll = isUserList && string.IsNullOrEmpty(resolvedAnimeId);
            var startOffset = int.TryParse(skip, out var requestedSkip) ? requestedSkip : 0;

            // Stremio scrolls a catalog by repeatedly hitting GetList with rising `skip`
            // values; without a cache, every scroll re-runs the entire fetch-all loop.
            // Serve repeats from the per-user cache and only paginate the slice.
            if (fetchAll && TryGetCachedUserList(tokenData.user_id, list, genre, out var cachedFull))
                return cachedFull.Skip(startOffset).Take(CatalogPageSize).ToList();

            await _mappingService.EnsureLoadedAsync();
            var seenIds = new Dictionary<string, Meta>();

            // First page is sequential — for the fetch-all path we need its meta.count to
            // fan out the remaining pages in parallel.
            var firstSkip = fetchAll ? "0" : skip;
            var firstUrl = BuildListUrl(tokenData, list, firstSkip, resolvedAnimeId, genre, search, sort, isUserList);
            var firstPage = await FetchKitsuPageAsync(firstUrl, tokenData);
            if (firstPage == null) return [];

            await ProcessKitsuPageAsync(firstPage, list, isUserList, genre, seenIds);

            if (fetchAll)
            {
                // Kitsu caps page[limit] at 20, so a 1000-entry library would otherwise need
                // 50 serial round-trips (~7-10s on a cold view). Use meta.count to know the
                // total upfront and fire the remaining pages concurrently. SemaphoreSlim caps
                // the in-flight count so a power user can't hammer Kitsu's rate limit.
                var totalCount = (int?)firstPage["meta"]?["count"];
                var firstDataCount = (firstPage["data"] as JArray)?.Count ?? 0;

                // Track partial failures so we don't poison the cache with an incomplete
                // list — a cached short list would persist for the whole TTL.
                bool fetchComplete = true;

                if (totalCount.HasValue && totalCount.Value > CatalogPageSize)
                {
                    using var sem = new SemaphoreSlim(8);
                    var tasks = new List<Task<JObject>>();
                    for (int offset = CatalogPageSize; offset < totalCount.Value; offset += CatalogPageSize)
                    {
                        var url = BuildListUrl(tokenData, list, offset.ToString(), resolvedAnimeId, genre, search, sort, isUserList);
                        tasks.Add(FetchWithSemaphoreAsync(url, tokenData, sem));
                    }

                    var pages = await Task.WhenAll(tasks);
                    foreach (var page in pages)
                    {
                        if (page == null) { fetchComplete = false; continue; }
                        await ProcessKitsuPageAsync(page, list, isUserList, genre, seenIds);
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
                        var url = BuildListUrl(tokenData, list, offset.ToString(), resolvedAnimeId, genre, search, sort, isUserList);
                        var page = await FetchKitsuPageAsync(url, tokenData);
                        if (page == null) { fetchComplete = false; break; }

                        var dataCount = (page["data"] as JArray)?.Count ?? 0;
                        if (dataCount == 0) break;

                        await ProcessKitsuPageAsync(page, list, isUserList, genre, seenIds);
                        if (dataCount < CatalogPageSize) break;
                        offset += CatalogPageSize;
                    }
                }

                var fullList = seenIds.Values.ToList();
                if (fetchComplete) SetCachedUserList(tokenData.user_id, list, genre, fullList);

                return fullList.Skip(startOffset).Take(CatalogPageSize).ToList();
            }

            return seenIds.Values.ToList();
        }

        // ── User-list cache helpers ───────────────────────────────────────
        // Kept as static class-level methods so the dictionary lives across the per-request
        // scoped lifetime of KitsuService and is shared between the read and write paths.

        private static string BuildUserListCacheKey(string userId, ListType? list, string genre)
            => $"{userId}|{list?.ToString() ?? "all"}|{genre ?? ""}";

        private static bool TryGetCachedUserList(string userId, ListType? list, string genre, out List<Meta> metas)
        {
            metas = null;
            if (string.IsNullOrEmpty(userId)) return false;
            var key = BuildUserListCacheKey(userId, list, genre);
            if (_userListCache.TryGetValue(key, out var entry))
            {
                if (DateTime.UtcNow < entry.Expiry)
                {
                    metas = entry.Metas;
                    return true;
                }
                _userListCache.TryRemove(key, out _);
            }
            return false;
        }

        private static void SetCachedUserList(string userId, ListType? list, string genre, List<Meta> metas)
        {
            if (string.IsNullOrEmpty(userId)) return;
            var key = BuildUserListCacheKey(userId, list, genre);
            _userListCache[key] = (DateTime.UtcNow + UserListCacheTtl, metas);
        }

        // Save / delete change at least one list and may shift entries between lists, so
        // wipe every cached key for the user rather than try to figure out which combinations
        // remain valid. Cheap because key lookup is O(N) over a small dict.
        private static void InvalidateUserListCache(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return;
            var prefix = userId + "|";
            foreach (var key in _userListCache.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    _userListCache.TryRemove(key, out _);
            }
        }

        private async Task<JObject> FetchKitsuPageAsync(string url, TokenData tokenData)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(tokenData?.access_token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

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
        private async Task ProcessKitsuPageAsync(JObject json, ListType? list, bool isUserList, string genre, Dictionary<string, Meta> seenIds)
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
                        var title = (string)inc["attributes"]?["title"];
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

                if (isUserList)
                {
                    var animeRefId = (string)entry["relationships"]?["anime"]?["data"]?["id"];
                    if (string.IsNullOrEmpty(animeRefId) || !includedAnime.TryGetValue(animeRefId, out anime)) continue;
                    entryId = (string)entry["id"];
                    entryStatus = (string)entry["attributes"]?["status"];
                }
                else
                {
                    anime = entry;
                }

                var status = (string)anime["attributes"]?["status"];
                if (list == ListType.Current && status is "tba" or "unreleased" or "upcoming") continue;

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

                var externalId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                                 !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                                 $"{kitsuPrefix}{animeKitsuId}";

                var subtype = (string)anime["attributes"]?["subtype"];
                var isMovie = IsMovieFormat(subtype);

                var meta = new Meta((string)anime["attributes"]?["description"])
                {
                    id = externalId,
                    type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = ExtractTitle(anime),
                    poster = (string)anime["attributes"]?["posterImage"]?["large"],
                    entryId = entryId,
                    entryStatus = entryStatus,
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

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(id, AnimeService.Kitsu);
            if (string.IsNullOrEmpty(resolvedAnimeId)) return null;

            // animeProductions.producer pulls each studio/producer relationship and the
            // related producer node (name + slug) so we can surface Studio links the same
            // way AniList and MAL do.
            var url = $"{_kitsuApi}/anime/{resolvedAnimeId}?include=categories,episodes,animeProductions.producer";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(tokenData?.access_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            if (json["data"] is not JObject entry) return null;

            var mapping = await _mappingService.GetKitsuMapping(resolvedAnimeId);

            var externalId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                             !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                             $"{kitsuPrefix}{(string)entry["id"]}";

            var subtype = (string)entry["attributes"]?["subtype"];
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
                        var catTitle = (string)inc["attributes"]?["title"];
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
                        var pname = (string)inc["attributes"]?["name"];
                        var pslug = (string)inc["attributes"]?["slug"];
                        if (!string.IsNullOrEmpty(pid) && !string.IsNullOrEmpty(pname))
                            producers[pid] = (pname, pslug);
                    }
                }
            }

            var anime = new Meta((string)entry["attributes"]?["description"])
            {
                id = externalId,
                type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = ExtractTitle(entry),
                poster = (string)entry["attributes"]?["posterImage"]?["large"],
                background = (string)entry["attributes"]?["coverImage"]?["original"]
                             ?? (string)entry["attributes"]?["coverImage"]?["large"]
                             ?? (string)entry["attributes"]?["posterImage"]?["original"],
                genres = categoryTitles.Count > 0 ? categoryTitles : null,
            };

            var youtubeId = (string)entry["attributes"]?["youtubeVideoId"];
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
                if ((string)prod["attributes"]?["role"] != "studio") continue;
                var producerId = (string)prod["relationships"]?["producer"]?["data"]?["id"];
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
                var sortedEpisodes = episodes
                    .OrderBy(e => (int?)e["attributes"]?["seasonNumber"] ?? 1)
                    .ThenBy(e => (int?)e["attributes"]?["number"] ?? 0)
                    .ToList();

                int episodeNumber = 1;
                foreach (var episode in sortedEpisodes)
                {
                    var seasonNumber = (int?)episode["attributes"]?["seasonNumber"] ?? 1;
                    var thumbnail = (string)episode["attributes"]?["thumbnail"]?["original"]
                                    ?? (string)episode["attributes"]?["thumbnail"]?["large"];
                    var epTitle = (string)episode["attributes"]?["canonicalTitle"];

                    anime.videos.Add(new Video
                    {
                        id = $"{externalId}:{episodeNumber}",
                        title = string.IsNullOrEmpty(epTitle) ? $"Episode {episodeNumber}" : epTitle,
                        thumbnail = thumbnail,
                        season = seasonNumber > 0 ? seasonNumber : 1,
                        episode = episodeNumber,
                    });
                    episodeNumber++;
                }
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
                }
                catch
                {
                    // best-effort enrichment
                }
            }

            return anime;
        }

        public async Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu, season);
            if (string.IsNullOrEmpty(resolvedKitsuId)) return null;

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
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

            var response = await _clientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                entry.TotalEpisodes = await GetTotalEpisodesAsync(tokenData, resolvedKitsuId);
                return entry;
            }

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            if ((json["data"] as JArray)?.OfType<JObject>().FirstOrDefault() is JObject libEntry)
            {
                var attrs = libEntry["attributes"];
                entry.EntryId = (string)libEntry["id"];
                entry.Status = (string)attrs?["status"];
                entry.Progress = (int?)attrs?["progress"] ?? 0;
                // Kitsu's ratingTwenty is 2-20 (where 20 == 10/10); convert to a 0-10 scale.
                var ratingTwenty = (int?)attrs?["ratingTwenty"];
                entry.Score = ratingTwenty.HasValue ? ratingTwenty.Value / 2.0 : null;
                entry.Notes = (string)attrs?["notes"];
                entry.RewatchCount = (int?)attrs?["reconsumeCount"] ?? 0;
                entry.StartedAt = ParseKitsuDate((string)attrs?["startedAt"]);
                entry.FinishedAt = ParseKitsuDate((string)attrs?["finishedAt"]);
            }

            entry.TotalEpisodes = (int?)(json["included"] as JArray)?
                .OfType<JObject>()
                .FirstOrDefault(i => (string)i["type"] == "anime")?["attributes"]?["episodeCount"]
                ?? await GetTotalEpisodesAsync(tokenData, resolvedKitsuId);

            return entry;
        }

        public async Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu, season);
            if (string.IsNullOrEmpty(resolvedKitsuId)) return;
            if (string.IsNullOrEmpty(tokenData?.access_token) || string.IsNullOrEmpty(tokenData?.user_id)) return;

            var existing = await GetAnimeEntryAsync(tokenData, $"{kitsuPrefix}{resolvedKitsuId}", season);

            if (string.IsNullOrEmpty(status)) status = existing?.Status;
            // Kitsu requires a status when creating; default new entries to current
            if (string.IsNullOrEmpty(status)) status = GetListTypeString(ListType.Current, tokenData);

            // Build a sparse attributes dict so unset fields aren't overwritten with null
            var attributes = new Dictionary<string, object>
            {
                ["progress"] = progress,
                ["status"] = status,
            };
            if (score.HasValue)
                attributes["ratingTwenty"] = (int)Math.Round(Math.Clamp(score.Value, 0, 10) * 2);
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

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
            await _clientFactory.CreateClient().SendAsync(request);

            // The user just changed (or added) an entry — invalidate every cached list for
            // them so the next browse reflects the change immediately rather than waiting
            // out the TTL.
            InvalidateUserListCache(tokenData.user_id);
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
            var episodeCount = (int?)entry["attributes"]?["episodeCount"];
            return (name, episodeCount);
        }

        public async Task<List<StreamingLink>> GetExternalLinksAsync(string animeId, TokenData tokenData)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu);
            if (string.IsNullOrEmpty(resolvedKitsuId)) return [];

            var url = $"{_kitsuApi}/anime/{resolvedKitsuId}?include=streamingLinks.streamer&fields[anime]=id&fields[streamingLinks]=url,streamer&fields[streamers]=siteName";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(tokenData?.access_token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

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
                    var name = (string)inc["attributes"]?["siteName"];
                    if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(name)) streamers[sid] = name;
                }
            }

            var result = new List<StreamingLink>();
            if (json["included"] is JArray included2)
            {
                foreach (var inc in included2.OfType<JObject>())
                {
                    if ((string)inc["type"] != "streamingLinks") continue;
                    var linkUrl = (string)inc["attributes"]?["url"];
                    if (string.IsNullOrEmpty(linkUrl)) continue;

                    var streamerId = (string)inc["relationships"]?["streamer"]?["data"]?["id"];
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
            var existing = await GetAnimeEntryAsync(tokenData, $"{kitsuPrefix}{resolvedKitsuId}", season);
            if (string.IsNullOrEmpty(existing?.EntryId)) return;

            var request = new HttpRequestMessage(HttpMethod.Delete, $"{_kitsuApi}/library-entries/{existing.EntryId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);

            await _clientFactory.CreateClient().SendAsync(request);

            // Drop the user's cached lists so the removal shows up on the next browse.
            InvalidateUserListCache(tokenData.user_id);
        }

        private static DateTime? ParseKitsuDate(string raw)
        {
            return DateTime.TryParse(raw, out var dt) ? dt : null;
        }

        private async Task<int?> GetTotalEpisodesAsync(TokenData tokenData, string resolvedKitsuId)
        {
            var url = $"{_kitsuApi}/anime/{resolvedKitsuId}?fields[anime]=episodeCount";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(tokenData?.access_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

            return (int?)json["data"]?["attributes"]?["episodeCount"];
        }

        private string BuildListUrl(TokenData tokenData, ListType? list, string skip, string resolvedAnimeId, string genre, string search, string sort, bool isUserList)
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
                var (season, year) = GetSeasonAndYear(genre ?? SeasonCurrent);
                var sortValue = string.IsNullOrEmpty(sort) ? "-userCount" : SortToKitsu(sort);
                url = $"{_kitsuApi}/anime?sort={sortValue}&filter[season]={season.ToLowerInvariant()}&filter[seasonYear]={year}&include=categories";
            }
            else if (list == ListType.Search)
            {
                url = $"{_kitsuApi}/anime?include=categories&filter[text]={Uri.EscapeDataString(search ?? string.Empty)}";
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

            var refs = anime["relationships"]?["categories"]?["data"] as JArray;
            if (refs == null) return null;

            return refs.OfType<JObject>()
                .Select(c => (string)c["id"])
                .Where(id => !string.IsNullOrEmpty(id) && categoryNames.ContainsKey(id))
                .Select(id => categoryNames[id])
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }

        private static string ExtractTitle(JObject anime)
        {
            return (string)anime["attributes"]?["titles"]?["en"]
                ?? (string)anime["attributes"]?["titles"]?["en_jp"]
                ?? (string)anime["attributes"]?["canonicalTitle"];
        }
    }
}
