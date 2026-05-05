using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http.Headers;

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
        private const string NodeFields = "id,title,main_picture,media_type,status,num_episodes,genres,synopsis,alternative_titles";
        // /users/@me/animelist returns the user's list metadata in `list_status`.
        private const string UserListStatusFields = "list_status{status,score,num_episodes_watched,is_rewatching,num_times_rewatched,start_date,finish_date,comments}";
        // /anime/{id} returns the same data in `my_list_status` when authenticated.
        private const string MyListStatusFields = "my_list_status{status,score,num_episodes_watched,is_rewatching,num_times_rewatched,start_date,finish_date,comments}";

        public MalService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService,
            IAnilistFallback anilistFallback, IConfiguration configuration, IKitsuService kitsuService)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _anilistFallback = anilistFallback;
            _configuration = configuration;
            _kitsuService = kitsuService;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null)
        {
            // MAL has no airing schedule endpoint, so use the AniList fallback like Kitsu does.
            if (list == ListType.Airing)
                return await _anilistFallback.GetAiringScheduleAsync(AnimeService.MyAnimeList, skip);

            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.MyAnimeList);
            var isUserList = !list.HasValue || _userLists.Contains(list.Value);

            // User-list endpoints require a logged-in user.
            if (isUserList && string.IsNullOrEmpty(tokenData?.access_token))
                return [];

            // Single-anime user-list lookup: MAL's /users/@me/animelist endpoint can't filter
            // by anime id, so hit the anime detail with my_list_status and synthesize the result.
            if (isUserList && !string.IsNullOrEmpty(resolvedAnimeId))
                return await GetSingleUserListEntryAsync(resolvedAnimeId, tokenData);

            // For "browse my list" requests we walk every page server-side and dedup across
            // all of them. The user-list catalogs no longer carry a `skip` extra in the
            // manifest (one request per catalog open), so the deduped result is returned in
            // full at the end rather than sliced. Mirrors the AnilistService MediaListCollection
            // approach.
            var fetchAll = isUserList;
            // MAL caps animelist at 1000/page, so a single round-trip covers most users; the
            // ranking/seasonal/search endpoints stay at CatalogPageSize.
            var pageSize = fetchAll ? FullFetchPageSize : CatalogPageSize;

            await _mappingService.EnsureLoadedAsync();
            var seenIds = new Dictionary<string, Meta>();
            int apiOffset = 0;

            while (true)
            {
                var apiSkip = fetchAll ? apiOffset.ToString() : skip;
                var url = BuildListUrl(list, apiSkip, genre, search, sort, isUserList, pageSize);
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
                    if ((list == ListType.Current || list == ListType.Repeating) && status == "not_yet_aired")
                        continue;

                    // Genre post-filter — MAL's list/ranking endpoints have no server-side genre
                    // arg, so filter the response. Skip for Seasonal because the catalog repurposes
                    // the `genre` extra as a season selector ("This Season" / "Next Season" / etc.)
                    // and matching that against an anime's genres array would zero out the catalog.
                    if (list != ListType.Seasonal && !string.IsNullOrEmpty(genre) && !MatchesGenre(node, genre))
                        continue;

                    var meta = await BuildMetaAsync(node, listStatus);
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

            return seenIds.Values.ToList();
        }

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData)
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

            var externalId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                             !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                             $"{malPrefix}{(string)json["id"]}";

            var mediaType = (string)json["media_type"];
            var isMovie = IsMovieFormat(mediaType);

            var anime = new Meta((string)json["synopsis"])
            {
                id = externalId,
                type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = ExtractTitle(json),
                poster = (string)json["main_picture"]?["large"] ?? (string)json["main_picture"]?["medium"],
                background = (json["pictures"] as JArray)?.OfType<JObject>().FirstOrDefault()?["large"]?.ToString()
                             ?? (string)json["main_picture"]?["large"],
                genres = (json["genres"] as JArray)?
                    .OfType<JObject>()
                    .Select(g => (string)g["name"])
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList(),
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
                }
            }

            if (!isMovie)
            {
                var episodeCount = (int?)json["num_episodes"] ?? 0;
                if (episodeCount > 0)
                {
                    for (int i = 1; i <= episodeCount; i++)
                    {
                        anime.videos.Add(new Video
                        {
                            id = $"{externalId}:{i}",
                            title = $"Episode {i}",
                            season = 1,
                            episode = i,
                        });
                    }
                }
                else if (mapping?.KitsuId != null)
                {
                    // MAL only gives us a count; defer to Kitsu when we want per-episode titles.
                    var fallback = await _kitsuService.GetAnimeByIdAsync($"{kitsuPrefix}{mapping.KitsuId}", null);
                    anime.videos = fallback?.videos ?? [];
                }
            }

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
                entry.Score = (double?)mls["score"];
                entry.Notes = (string)mls["comments"];
                entry.RewatchCount = (int?)mls["num_times_rewatched"] ?? 0;
                entry.StartedAt = ParseMalDate((string)mls["start_date"]);
                entry.FinishedAt = ParseMalDate((string)mls["finish_date"]);
            }

            return entry;
        }

        public async Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null)
        {
            if (string.IsNullOrEmpty(tokenData?.access_token)) return;

            var resolvedMalId = await _mappingService.GetIdByService(animeId, AnimeService.MyAnimeList, season);
            if (string.IsNullOrEmpty(resolvedMalId)) return;

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
            ThrowIfApiCallFailed(saveResponse, "save");
        }

        public async Task DeleteAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null)
        {
            if (string.IsNullOrEmpty(tokenData?.access_token)) return;

            var resolvedMalId = await _mappingService.GetIdByService(animeId, AnimeService.MyAnimeList, season);
            if (string.IsNullOrEmpty(resolvedMalId)) return;

            var request = new HttpRequestMessage(HttpMethod.Delete, $"{MalApi}/anime/{resolvedMalId}/my_list_status");
            ApplyAuth(request, tokenData);
            var deleteResponse = await _clientFactory.CreateClient().SendAsync(request);
            ThrowIfApiCallFailed(deleteResponse, "delete");
        }

        // MAL silently dropped 401s before — letting them bubble up gives SyncService a
        // chance to flag NeedsReauth on a linked-account row whose token has been revoked.
        private static void ThrowIfApiCallFailed(HttpResponseMessage response, string op)
        {
            if (response.IsSuccessStatusCode) return;
            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException($"MyAnimeList {op} returned {(int)response.StatusCode}");
            throw new HttpRequestException($"MyAnimeList {op} returned {(int)response.StatusCode}");
        }

        private async Task<Meta> BuildMetaAsync(JObject node, JObject listStatus)
        {
            var malIdRaw = node["id"];
            if (malIdRaw == null) return null;
            var malIdStr = (string)malIdRaw;

            var mapping = await _mappingService.GetMalMapping($"{malPrefix}{malIdStr}");

            var externalId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                             !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" :
                             $"{malPrefix}{malIdStr}";

            var mediaType = (string)node["media_type"];
            var isMovie = IsMovieFormat(mediaType);

            return new Meta((string)node["synopsis"])
            {
                id = externalId,
                type = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString(),
                name = ExtractTitle(node),
                poster = (string)node["main_picture"]?["large"] ?? (string)node["main_picture"]?["medium"],
                entryId = listStatus != null ? malIdStr : null,
                entryStatus = (string)listStatus?["status"],
            };
        }

        private string BuildListUrl(ListType? list, string skip, string genre, string search, string sort, bool isUserList, int pageSize)
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
                var (season, year) = GetSeasonAndYear(genre ?? SeasonCurrent);
                var seasonLower = season.ToLowerInvariant();
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
        private async Task<List<Meta>> GetSingleUserListEntryAsync(string resolvedMalId, TokenData tokenData)
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
            var meta = await BuildMetaAsync(json, listStatus);
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
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
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
            return (string)anime["alternative_titles"]?["en"]
                ?? (string)anime["title"];
        }

        private static DateTime? ParseMalDate(string raw)
        {
            return DateTime.TryParse(raw, out var dt) ? dt : null;
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
