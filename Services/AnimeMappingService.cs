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
        private FrozenDictionary<int, AnimeIdMapping> _anilistMapping;
        private FrozenDictionary<int, AnimeIdMapping> _kitsuMapping;
        private FrozenDictionary<string, AnimeIdMapping> _imdbMapping;
        private FrozenDictionary<string, AnimeIdMapping> _tmdbMapping;
        private DateTime _lastLoaded = DateTime.MinValue;

        public AnimeMappingService(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory;
        }

        public async Task<AnimeIdMapping> GetAnilistMapping(string anilistId)
        {
            await EnsureMappingsLoadedAsync();
            return _anilistMapping.TryGetValue(int.Parse(anilistId.Replace(anilistPrefix, "")), out var mapping) ? mapping : null;
        }

        public async Task<AnimeIdMapping> GetKitsuMapping(string kitsuId)
        {
            await EnsureMappingsLoadedAsync();
            return _kitsuMapping.TryGetValue(int.Parse(kitsuId.Replace(kitsuPrefix, "")), out var mapping) ? mapping : null;
        }

        public async Task<AnimeIdMapping> GetImdbMapping(string imdb)
        {
            await EnsureMappingsLoadedAsync();
            return _imdbMapping.TryGetValue(imdb, out var mapping) ? mapping : null;
        }

        public async Task<AnimeIdMapping> GetTmdbMapping(string tmdbId)
        {
            await EnsureMappingsLoadedAsync();
            return _tmdbMapping.TryGetValue(tmdbId.Replace(tmdbPrefix, ""), out var mapping) ? mapping : null;
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

            if (_anilistMapping is not null && DateTime.UtcNow - _lastLoaded < CacheDuration)
                return;

            var client = _clientFactory.CreateClient();
            var json = await client.GetStringAsync(MappingUrl);
            var entries = DeserializeObject<List<AnimeIdMapping>>(json) ?? [];

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
                .DistinctBy(e => e.ImdbId)
                .ToFrozenDictionary(e => e.ImdbId, e => e);

            _tmdbMapping = entries
                .Where(e => !string.IsNullOrEmpty(e.TmdbId))
                .DistinctBy(e => e.TmdbId)
                .ToFrozenDictionary(e => e.TmdbId, e => e);

            _lastLoaded = DateTime.UtcNow;
            _semaphore.Release();
        }

        public async Task<string> GetIdByService(string animeId, AnimeService service)
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
                var mapping = await GetImdbMapping(animeId);
                return service == AnimeService.Anilist ? mapping?.AnilistId?.ToString() : mapping?.KitsuId?.ToString();
            }
            else if (animeId.StartsWith(tmdbPrefix))
            {
                var mapping = await GetTmdbMapping(animeId);
                return service == AnimeService.Anilist ? mapping?.AnilistId?.ToString() : mapping?.KitsuId?.ToString();
            }

            return null;
        }
    }
}
