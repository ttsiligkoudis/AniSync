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
        private FrozenDictionary<int, string> _anilistToImdb;
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

        public async Task<string> GetImdbIdByAnilistIdAsync(int anilistId)
        {
            await EnsureMappingsLoadedAsync();
            return _anilistToImdb.TryGetValue(anilistId, out var imdbId) ? imdbId : null;
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
            if (_anilistToImdb is not null && DateTime.UtcNow - _lastLoaded < CacheDuration)
                return;

            await _semaphore.WaitAsync();
            try
            {
                if (_anilistToImdb is not null && DateTime.UtcNow - _lastLoaded < CacheDuration)
                    return;

                var client = _clientFactory.CreateClient();
                var json = await client.GetStringAsync(MappingUrl);
                var entries = DeserializeObject<List<AnimeIdMapping>>(json) ?? [];

                var withImdb = entries.Where(e => !string.IsNullOrEmpty(e.ImdbId)).ToList();

                _anilistToImdb = withImdb
                    .Where(e => e.AnilistId.HasValue && !string.IsNullOrEmpty(e.ImdbId))
                    .DistinctBy(e => new { e.AnilistId!.Value, e.ImdbId })
                    .ToFrozenDictionary(e => e.AnilistId!.Value, e => e.ImdbId);

                _kitsuToImdb = withImdb
                    .Where(e => e.KitsuId.HasValue && !string.IsNullOrEmpty(e.ImdbId))
                    .DistinctBy(e => new { e.KitsuId!.Value, e.ImdbId })
                    .ToFrozenDictionary(e => e.KitsuId!.Value, e => e.ImdbId);

                _imdbToAnilist = withImdb
                    .Where(e => !string.IsNullOrEmpty(e.ImdbId) && e.AnilistId.HasValue)
                    .DistinctBy(e => new { e.ImdbId, e.AnilistId })
                    .ToFrozenDictionary(e => e.ImdbId, e => e.AnilistId!.Value);

                _imdbToKitsu = withImdb
                    .Where(e => !string.IsNullOrEmpty(e.ImdbId) && e.KitsuId.HasValue)
                    .DistinctBy(e => new { e.ImdbId, e.KitsuId })
                    .ToFrozenDictionary(e => e.ImdbId, e => e.KitsuId!.Value);

                _anilistToKitsu = entries
                    .Where(e => e.AnilistId.HasValue && e.KitsuId.HasValue)
                    .DistinctBy(e => new { e.AnilistId!.Value, KitsuId = e.KitsuId!.Value })
                    .ToFrozenDictionary(e => e.AnilistId!.Value, e => e.KitsuId!.Value);

                _kitsuToAnilist = entries
                    .Where(e => e.KitsuId.HasValue && e.AnilistId.HasValue)
                    .DistinctBy(e => new { e.KitsuId!.Value, AnilistId = e.AnilistId!.Value })
                    .ToFrozenDictionary(e => e.KitsuId!.Value, e => e.AnilistId!.Value);

                _lastLoaded = DateTime.UtcNow;
            }
            catch
            {
                _anilistToImdb ??= FrozenDictionary<int, string>.Empty;
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

        public async Task<int?> GetIdByService(string animeId, AnimeService service)
        {
            if (string.IsNullOrEmpty(animeId))
                return null;

            if (animeId.StartsWith(imdbPrefix))
                return service == AnimeService.Anilist ? await GetAnilistIdByImdbIdAsync(animeId) : await GetKitsuIdByImdbIdAsync(animeId);
            else if (animeId.StartsWith(anilistPrefix))
                return service == AnimeService.Kitsu ? await GetKitsuIdByAnilistIdAsync(int.Parse(animeId.Replace(anilistPrefix, ""))) : int.Parse(animeId.Replace(anilistPrefix, ""));
            else if (animeId.StartsWith(kitsuPrefix))
                return service == AnimeService.Anilist ? await GetAnilistIdByKitsuIdAsync(int.Parse(animeId.Replace(kitsuPrefix, ""))) : int.Parse(animeId.Replace(kitsuPrefix, ""));

            return null;
        }
    }
}
