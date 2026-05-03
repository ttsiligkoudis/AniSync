using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
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

            string url = BuildListUrl(tokenData, list, skip, resolvedAnimeId, genre, search, sort, isUserList);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(tokenData?.access_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.access_token);
            }

            var client = _clientFactory.CreateClient();
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return [];

            var content = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(content);

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

            await _mappingService.EnsureLoadedAsync();

            var seenIds = new Dictionary<string, Meta>();
            if (json["data"] is not JArray dataArr) return [];

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

            return seenIds.Values.ToList();
        }

        public async Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(id, AnimeService.Kitsu);
            if (string.IsNullOrEmpty(resolvedAnimeId)) return null;

            var url = $"{_kitsuApi}/anime/{resolvedAnimeId}?include=categories,episodes";
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

            // Pull categories and episodes out of the included array
            var categoryTitles = new List<string>();
            var episodes = new List<JObject>();
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
            if (!string.IsNullOrEmpty(youtubeId))
            {
                anime.trailers.Add(new Trailer(youtubeId));
                anime.trailerStreams.Add(new TrailerStream(youtubeId, anime.name));
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
            // in the mapping; the fallback rewrites ids back to Kitsu where possible.
            if (mapping?.AnilistId != null)
            {
                var similar = await _anilistFallback.GetRecommendationsAsync(mapping.AnilistId.Value, AnimeService.Kitsu);
                anime.links.AddRange(similar);
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
