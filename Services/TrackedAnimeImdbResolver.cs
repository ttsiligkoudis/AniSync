using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Default <see cref="ITrackedAnimeImdbResolver"/>: reads the user's watching cache (their primary
    /// anime-service ids) and resolves each to its IMDb id via the cross-service mapping, so Trakt "series"
    /// entries that are really anime the user already tracks can be filtered out of the Calendar and the
    /// series-episode notifications.
    /// </summary>
    public class TrackedAnimeImdbResolver : ITrackedAnimeImdbResolver
    {
        private readonly IWatchingCacheStore _watchingCache;
        private readonly IAnimeMappingService _mapping;

        public TrackedAnimeImdbResolver(IWatchingCacheStore watchingCache, IAnimeMappingService mapping)
        {
            _watchingCache = watchingCache;
            _mapping = mapping;
        }

        public async Task<HashSet<string>> GetTrackedAnimeImdbIdsAsync(string uid, CancellationToken ct = default)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(uid)) return result;

            var cache = await _watchingCache.GetAsync(uid);
            if (cache == null || cache.MediaIds.Count == 0) return result;
            // A Trakt-primary user IS tracking through Trakt — there's no separate anime list to dedupe
            // against, so don't strip anything (the cached ids aren't anime-service ids anyway).
            if (cache.Service is not (AnimeService.Anilist or AnimeService.MyAnimeList or AnimeService.Kitsu))
                return result;

            foreach (var id in cache.MediaIds)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    // The cache ids are in the user's primary-service space; the matching Get*Mapping
                    // strips the prefix and enriches ImdbId on demand. A mapping gap (no imdb) just means
                    // that title can't collide with a Trakt imdb entry, so skip it silently.
                    var m = cache.Service switch
                    {
                        AnimeService.Anilist => await _mapping.GetAnilistMapping(id),
                        AnimeService.MyAnimeList => await _mapping.GetMalMapping(id),
                        AnimeService.Kitsu => await _mapping.GetKitsuMapping(id),
                        _ => null,
                    };
                    if (m != null && !string.IsNullOrEmpty(m.ImdbId)
                        && m.ImdbId.StartsWith("tt", StringComparison.Ordinal))
                        result.Add(m.ImdbId);
                }
                catch { /* mapping miss — leave this title out of the exclusion set */ }
            }

            return result;
        }
    }
}
