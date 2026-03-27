using AnimeList.Models;
using AnimeList.Services.Interfaces;
using System.Collections.Frozen;

namespace AnimeList.Services
{
    /// <summary>
    /// Resolves IMDb IDs from anime database IDs using the Fribb/anime-lists community mapping.
    /// Registered as a singleton; caches the full mapping in memory for 24 hours.
    /// </summary>
    public class AnimeMappingService : IAnimeMappingService
    {
        private const string MappingUrl =
            "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json";

        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

        private readonly IHttpClientFactory _clientFactory;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private FrozenDictionary<int, string> _malToImdb;
        private FrozenDictionary<int, string> _kitsuToImdb;
        private FrozenDictionary<string, int> _imdbToAnilist;
        private FrozenDictionary<string, int> _imdbToKitsu;
        private FrozenDictionary<int, int> _anilistToKitsu;
        private FrozenDictionary<int, int> _kitsuToAnilist;
        private DateTime _lastLoaded = DateTime.MinValue;

        public AnimeMappingService(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory;
        }

        public async Task<string> GetImdbIdByMalIdAsync(int malId)
        {
            await EnsureMappingsLoadedAsync();
            return _malToImdb.TryGetValue(malId, out var imdbId) ? imdbId : null;
        }

        public async Task<string> GetImdbIdByKitsuIdAsync(int kitsuId)
        {
            await EnsureMappingsLoadedAsync();
            return _kitsuToImdb.TryGetValue(kitsuId, out var imdbId) ? imdbId : null;
        }

        public async Task<int?> GetAnilistIdByImdbIdAsync(string imdbId)
        {
            await EnsureMappingsLoadedAsync();
            return _imdbToAnilist.TryGetValue(imdbId, out var anilistId) ? anilistId : null;
        }

        public async Task<int?> GetKitsuIdByImdbIdAsync(string imdbId)
        {
            await EnsureMappingsLoadedAsync();
            return _imdbToKitsu.TryGetValue(imdbId, out var kitsuId) ? kitsuId : null;
        }

        public async Task<int?> GetKitsuIdByAnilistIdAsync(int anilistId)
        {
            await EnsureMappingsLoadedAsync();
            return _anilistToKitsu.TryGetValue(anilistId, out var kitsuId) ? kitsuId : null;
        }

        public async Task<int?> GetAnilistIdByKitsuIdAsync(int kitsuId)
        {
            await EnsureMappingsLoadedAsync();
            return _kitsuToAnilist.TryGetValue(kitsuId, out var anilistId) ? anilistId : null;
        }

        private async Task EnsureMappingsLoadedAsync()
        {
            if (_malToImdb is not null && DateTime.UtcNow - _lastLoaded < CacheDuration)
                return;

            await _semaphore.WaitAsync();
            try
            {
                if (_malToImdb is not null && DateTime.UtcNow - _lastLoaded < CacheDuration)
                    return;

                var client = _clientFactory.CreateClient();
                var json = await client.GetStringAsync(MappingUrl);
                var entries = DeserializeObject<List<AnimeIdMapping>>(json) ?? [];

                var withImdb = entries.Where(e => !string.IsNullOrEmpty(e.ImdbId)).ToList();

                _malToImdb = withImdb
                    .Where(e => e.MalId.HasValue)
                    .DistinctBy(e => e.MalId!.Value)
                    .ToFrozenDictionary(e => e.MalId!.Value, e => e.ImdbId);

                _kitsuToImdb = withImdb
                    .Where(e => e.KitsuId.HasValue)
                    .DistinctBy(e => e.KitsuId!.Value)
                    .ToFrozenDictionary(e => e.KitsuId!.Value, e => e.ImdbId);

                _imdbToAnilist = withImdb
                    .Where(e => e.AnilistId.HasValue)
                    .DistinctBy(e => e.ImdbId)
                    .ToFrozenDictionary(e => e.ImdbId, e => e.AnilistId!.Value);

                _imdbToKitsu = withImdb
                    .Where(e => e.KitsuId.HasValue)
                    .DistinctBy(e => e.ImdbId)
                    .ToFrozenDictionary(e => e.ImdbId, e => e.KitsuId!.Value);

                _anilistToKitsu = entries
                    .Where(e => e.AnilistId.HasValue && e.KitsuId.HasValue)
                    .DistinctBy(e => e.AnilistId!.Value)
                    .ToFrozenDictionary(e => e.AnilistId!.Value, e => e.KitsuId!.Value);

                _kitsuToAnilist = entries
                    .Where(e => e.KitsuId.HasValue && e.AnilistId.HasValue)
                    .DistinctBy(e => e.KitsuId!.Value)
                    .ToFrozenDictionary(e => e.KitsuId!.Value, e => e.AnilistId!.Value);

                _lastLoaded = DateTime.UtcNow;
            }
            catch
            {
                _malToImdb ??= FrozenDictionary<int, string>.Empty;
                _kitsuToImdb ??= FrozenDictionary<int, string>.Empty;
                _imdbToAnilist ??= FrozenDictionary<string, int>.Empty;
                _imdbToKitsu ??= FrozenDictionary<string, int>.Empty;
                _anilistToKitsu ??= FrozenDictionary<int, int>.Empty;
                _kitsuToAnilist ??= FrozenDictionary<int, int>.Empty;
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
