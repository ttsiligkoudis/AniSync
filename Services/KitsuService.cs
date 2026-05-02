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
        private readonly string _kitsuApi = "https://kitsu.io/api/edge";
        private readonly List<ListType> _userLists = [ListType.Current, ListType.Completed];

        // Kitsu enforces a maximum of 20 items per page
        private const int CatalogPageSize = 20;

        public KitsuService(IHttpClientFactory clientFactory, IAnimeMappingService mappingService)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
        }

        public async Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null)
        {
            var resolvedAnimeId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu);
            var isUserList = !list.HasValue || _userLists.Contains(list.Value);

            // User-list endpoints require authentication
            if (isUserList && string.IsNullOrEmpty(tokenData?.user_id))
                return [];

            string url = BuildListUrl(tokenData, list, skip, resolvedAnimeId, genre, isUserList);

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
                entry.EntryId = (string)libEntry["id"];
                entry.Status = (string)libEntry["attributes"]?["status"];
                entry.Progress = (int?)libEntry["attributes"]?["progress"] ?? 0;
            }

            entry.TotalEpisodes = (int?)(json["included"] as JArray)?
                .OfType<JObject>()
                .FirstOrDefault(i => (string)i["type"] == "anime")?["attributes"]?["episodeCount"]
                ?? await GetTotalEpisodesAsync(tokenData, resolvedKitsuId);

            return entry;
        }

        public async Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress, string status = null)
        {
            var resolvedKitsuId = await _mappingService.GetIdByService(animeId, AnimeService.Kitsu, season);
            if (string.IsNullOrEmpty(resolvedKitsuId)) return;
            if (string.IsNullOrEmpty(tokenData?.access_token) || string.IsNullOrEmpty(tokenData?.user_id)) return;

            var existing = await GetAnimeEntryAsync(tokenData, $"{kitsuPrefix}{resolvedKitsuId}", season);

            if (string.IsNullOrEmpty(status)) status = existing?.Status;
            // Kitsu requires a status when creating; default new entries to current
            if (string.IsNullOrEmpty(status)) status = GetListTypeString(ListType.Current, tokenData);

            var attributes = new { progress, status };

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

        private string BuildListUrl(TokenData tokenData, ListType? list, string skip, string resolvedAnimeId, string genre, bool isUserList)
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
                url = $"{_kitsuApi}/anime?sort=-userCount&filter[season]={season.ToLowerInvariant()}&filter[seasonYear]={year}&include=categories";
            }
            else
            {
                // Trending and other discovery lists: sort by popularity rank ascending (rank 1 = most popular)
                url = $"{_kitsuApi}/anime?sort=popularityRank&include=categories";
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
