using AnimeList.Models;
using AnimeList.Services.Interfaces;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net.Http.Headers;

namespace AnimeList.Services
{
    /// <summary>
    /// Resolves anime IDs across services (AniList, Kitsu, IMDb, TMDB) using community mapping data.
    /// Combines Fribb/anime-lists with manami-project/anime-offline-database for broader coverage,
    /// and enriches entries on demand via the TMDB external_ids API.
    /// Registered as a singleton; caches the full mapping in memory for 24 hours.
    /// </summary>
    public class AnimeMappingService : IAnimeMappingService
    {
        private const string FribbMappingUrl = "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json";
        private const string OfflineDbUrl = "https://raw.githubusercontent.com/manami-project/anime-offline-database/master/anime-offline-database-minified.json";
        private const string TmdbApiBase = "https://api.themoviedb.org/3";

        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

        private readonly IHttpClientFactory _clientFactory;
        private readonly IConfiguration _configuration;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly ConcurrentDictionary<string, string> _tmdbToImdbCache = new();
        private ConcurrentDictionary<string, List<AnimeIdMapping>> _enrichedImdbIndex = new();
        private FrozenDictionary<int, AnimeIdMapping> _anilistMapping;
        private FrozenDictionary<int, AnimeIdMapping> _kitsuMapping;
        private FrozenDictionary<string, List<AnimeIdMapping>> _imdbMapping;
        private FrozenDictionary<string, List<AnimeIdMapping>> _tmdbMapping;
        private DateTime _lastLoaded = DateTime.MinValue;

        public AnimeMappingService(IHttpClientFactory clientFactory, IConfiguration configuration)
        {
            _clientFactory = clientFactory;
            _configuration = configuration;
        }

        public async Task<AnimeIdMapping> GetAnilistMapping(string anilistId)
        {
            await EnsureMappingsLoadedAsync();
            if (!_anilistMapping.TryGetValue(int.Parse(anilistId.Replace(anilistPrefix, "")), out var mapping))
                return null;

            await TryEnrichImdbAsync(mapping);
            return mapping;
        }

        public async Task<AnimeIdMapping> GetKitsuMapping(string kitsuId)
        {
            await EnsureMappingsLoadedAsync();
            if (!_kitsuMapping.TryGetValue(int.Parse(kitsuId.Replace(kitsuPrefix, "")), out var mapping))
                return null;

            await TryEnrichImdbAsync(mapping);
            return mapping;
        }

        public async Task<List<AnimeIdMapping>> GetImdbMapping(string imdb, int? season = null)
        {
            await EnsureMappingsLoadedAsync();
            var entries = new List<AnimeIdMapping>();

            if (_imdbMapping.TryGetValue(imdb, out var frozenEntries))
                entries.AddRange(frozenEntries);

            if (_enrichedImdbIndex.TryGetValue(imdb, out var enrichedEntries))
                entries.AddRange(enrichedEntries);

            return entries
                .DistinctBy(e => (e.AnilistId, e.KitsuId))
                .Where(m => !season.HasValue || m.Season == season)
                .ToList();
        }

        public async Task<List<AnimeIdMapping>> GetTmdbMapping(string tmdbId, int? season = null)
        {
            await EnsureMappingsLoadedAsync();
            var entries = (_tmdbMapping.TryGetValue(tmdbId.Replace(tmdbPrefix, ""), out var mappings) ? mappings : []) ?? [];
            return entries.Where(m => !season.HasValue || m.Season == season).ToList();
        }

        public async Task EnsureLoadedAsync()
        {
            await EnsureMappingsLoadedAsync();
        }

        private async Task EnsureMappingsLoadedAsync()
        {
            if (_anilistMapping is not null && DateTime.UtcNow - _lastLoaded < CacheDuration)
                return;

            await _semaphore.WaitAsync();
            try
            {
                if (_anilistMapping is not null && DateTime.UtcNow - _lastLoaded < CacheDuration)
                    return;

                var client = _clientFactory.CreateClient();

                // Download primary and secondary sources in parallel
                var fribbTask = client.GetStringAsync(FribbMappingUrl);
                var offlineTask = DownloadSafeAsync(client, OfflineDbUrl);

                var fribbJson = await fribbTask;
                var offlineJson = await offlineTask;

                var entries = DeserializeObject<List<AnimeIdMapping>>(fribbJson) ?? [];

                // Merge anime-offline-database for broader AniList/Kitsu/MAL coverage
                if (offlineJson != null)
                    MergeOfflineDatabase(entries, offlineJson);

                _anilistMapping = entries
                    .Where(e => e.AnilistId.HasValue)
                    .DistinctBy(e => e.AnilistId!.Value)
                    .ToFrozenDictionary(e => e.AnilistId!.Value, e => e);

                _kitsuMapping = entries
                    .Where(e => e.KitsuId.HasValue)
                    .DistinctBy(e => e.KitsuId!.Value)
                    .ToFrozenDictionary(e => e.KitsuId!.Value, e => e);

                _imdbMapping = entries
                    .Where(e => !string.IsNullOrEmpty(e.ImdbId))
                    .GroupBy(e => e.ImdbId)
                    .ToFrozenDictionary(e => e.Key, e => e.ToList());

                _tmdbMapping = entries
                    .Where(e => !string.IsNullOrEmpty(e.TmdbId))
                    .GroupBy(e => e.TmdbId)
                    .ToFrozenDictionary(e => e.Key, e => e.ToList());

                // Clear enrichment caches; they reference stale entry objects after reload
                _enrichedImdbIndex = new ConcurrentDictionary<string, List<AnimeIdMapping>>();

                _lastLoaded = DateTime.UtcNow;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// If the mapping has a TMDB ID but no IMDb ID, resolves it via the TMDB external_ids API.
        /// Results are cached so each TMDB ID is fetched at most once.
        /// </summary>
        private async Task TryEnrichImdbAsync(AnimeIdMapping mapping)
        {
            if (mapping == null || !string.IsNullOrEmpty(mapping.ImdbId) || string.IsNullOrEmpty(mapping.TmdbId))
                return;

            if (_tmdbToImdbCache.TryGetValue(mapping.TmdbId, out var cachedImdb))
            {
                if (!string.IsNullOrEmpty(cachedImdb))
                    mapping.ImdbId = cachedImdb;
                return;
            }

            var imdbId = await FetchImdbFromTmdbAsync(mapping.TmdbId);
            _tmdbToImdbCache.TryAdd(mapping.TmdbId, imdbId ?? "");

            if (!string.IsNullOrEmpty(imdbId))
            {
                mapping.ImdbId = imdbId;
                _enrichedImdbIndex.AddOrUpdate(imdbId,
                    _ => [mapping],
                    (_, list) => { lock (list) { list.Add(mapping); } return list; });
            }
        }

        /// <summary>
        /// Calls TMDB /external_ids for TV first, then movie, to resolve an IMDb ID.
        /// </summary>
        private async Task<string> FetchImdbFromTmdbAsync(string tmdbId)
        {
            var token = _configuration["TmdbReadToken"];
            if (string.IsNullOrEmpty(token))
                return null;

            try
            {
                var client = _clientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                // Most anime are TV series; try that first
                var response = await client.GetAsync($"{TmdbApiBase}/tv/{tmdbId}/external_ids");
                if (response.IsSuccessStatusCode)
                {
                    var result = DeserializeObject<dynamic>(await response.Content.ReadAsStringAsync());
                    var imdb = (string)result?.imdb_id;
                    if (!string.IsNullOrEmpty(imdb))
                        return imdb;
                }

                // Fallback to movie
                response = await client.GetAsync($"{TmdbApiBase}/movie/{tmdbId}/external_ids");
                if (response.IsSuccessStatusCode)
                {
                    var result = DeserializeObject<dynamic>(await response.Content.ReadAsStringAsync());
                    var imdb = (string)result?.imdb_id;
                    if (!string.IsNullOrEmpty(imdb))
                        return imdb;
                }
            }
            catch
            {
                // TMDB enrichment is best-effort
            }

            return null;
        }

        private static async Task<string> DownloadSafeAsync(HttpClient client, string url)
        {
            try
            {
                return await client.GetStringAsync(url);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Merges anime-offline-database entries into the Fribb mapping list.
        /// Fills missing AniList, Kitsu, and MAL IDs on existing entries and adds new entries.
        /// </summary>
        private static void MergeOfflineDatabase(List<AnimeIdMapping> entries, string offlineJson)
        {
            var offlineDb = DeserializeObject<OfflineDbRoot>(offlineJson);
            if (offlineDb?.Data == null) return;

            var byAnilist = entries
                .Where(e => e.AnilistId.HasValue)
                .GroupBy(e => e.AnilistId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            var byKitsu = entries
                .Where(e => e.KitsuId.HasValue)
                .GroupBy(e => e.KitsuId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            var byMal = entries
                .Where(e => e.MalId.HasValue)
                .GroupBy(e => e.MalId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var offlineEntry in offlineDb.Data)
            {
                if (offlineEntry.Sources == null) continue;

                int? anilistId = null, kitsuId = null, malId = null;
                foreach (var source in offlineEntry.Sources)
                {
                    if (TryExtractId(source, "https://anilist.co/anime/", out var aid))
                        anilistId = aid;
                    else if (TryExtractId(source, "https://kitsu.app/anime/", out var kid))
                        kitsuId = kid;
                    else if (TryExtractId(source, "https://kitsu.io/anime/", out var kid2))
                        kitsuId = kid2;
                    else if (TryExtractId(source, "https://myanimelist.net/anime/", out var mid))
                        malId = mid;
                }

                if (!anilistId.HasValue && !kitsuId.HasValue && !malId.HasValue)
                    continue;

                // Find existing entry by any matching ID
                AnimeIdMapping existing = null;
                if (anilistId.HasValue) byAnilist.TryGetValue(anilistId.Value, out existing);
                if (existing == null && kitsuId.HasValue) byKitsu.TryGetValue(kitsuId.Value, out existing);
                if (existing == null && malId.HasValue) byMal.TryGetValue(malId.Value, out existing);

                if (existing != null)
                {
                    // Fill gaps in existing Fribb entry
                    if (!existing.AnilistId.HasValue && anilistId.HasValue)
                    {
                        existing.AnilistId = anilistId;
                        byAnilist.TryAdd(anilistId.Value, existing);
                    }
                    if (!existing.KitsuId.HasValue && kitsuId.HasValue)
                    {
                        existing.KitsuId = kitsuId;
                        byKitsu.TryAdd(kitsuId.Value, existing);
                    }
                    if (!existing.MalId.HasValue && malId.HasValue)
                    {
                        existing.MalId = malId;
                        byMal.TryAdd(malId.Value, existing);
                    }
                }
                else if (anilistId.HasValue || kitsuId.HasValue)
                {
                    var newEntry = new AnimeIdMapping
                    {
                        AnilistId = anilistId,
                        KitsuId = kitsuId,
                        MalId = malId
                    };
                    entries.Add(newEntry);
                    if (anilistId.HasValue) byAnilist.TryAdd(anilistId.Value, newEntry);
                    if (kitsuId.HasValue) byKitsu.TryAdd(kitsuId.Value, newEntry);
                    if (malId.HasValue) byMal.TryAdd(malId.Value, newEntry);
                }
            }
        }

        private static bool TryExtractId(string url, string prefix, out int id)
        {
            id = 0;
            if (!url.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var segment = url[prefix.Length..].TrimEnd('/');
            return int.TryParse(segment, out id);
        }

        public async Task<string> GetIdByService(string animeId, AnimeService service, int? season = null)
        {
            if (string.IsNullOrEmpty(animeId))
                return null;

            if (animeId.StartsWith(anilistPrefix))
            {
                if (service == AnimeService.Anilist)
                    return animeId.Replace(anilistPrefix, "");

                var mapping = await GetAnilistMapping(animeId);
                return mapping?.KitsuId?.ToString();
            }
            else if (animeId.StartsWith(kitsuPrefix))
            {
                var mapping = await GetKitsuMapping(animeId);
                return service == AnimeService.Anilist ? mapping?.AnilistId?.ToString() : mapping?.KitsuId?.ToString();
            } 
            else if (animeId.StartsWith(imdbPrefix))
            {
                var mapping = await GetImdbMapping(animeId, season);
                return service == AnimeService.Anilist ? mapping?.FirstOrDefault()?.AnilistId?.ToString() : mapping?.FirstOrDefault()?.KitsuId?.ToString();
            }
            else if (animeId.StartsWith(tmdbPrefix))
            {
                var mapping = await GetTmdbMapping(animeId, season);
                return service == AnimeService.Anilist ? mapping?.FirstOrDefault()?.AnilistId?.ToString() : mapping?.FirstOrDefault()?.KitsuId?.ToString();
            }

            return animeId;
        }

        private sealed class OfflineDbRoot
        {
            public List<OfflineDbEntry> Data { get; set; }
        }

        private sealed class OfflineDbEntry
        {
            public List<string> Sources { get; set; }
        }
    }
}
