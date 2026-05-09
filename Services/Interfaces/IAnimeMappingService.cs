using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Resolves IMDb IDs from anime database IDs and vice versa using community-maintained mapping data.
    /// </summary>
    public interface IAnimeMappingService
    {
        Task EnsureLoadedAsync();
        Task<AnimeIdMapping> GetAnilistMapping(string anilistId);
        Task<AnimeIdMapping> GetKitsuMapping(string kitsuId);
        Task<AnimeIdMapping> GetMalMapping(string malId);
        Task<List<AnimeIdMapping>> GetImdbMapping(string imdb, int? season = null);
        Task<List<AnimeIdMapping>> GetTmdbMapping(string tmdbId, int? season = null);
        Task<string> GetIdByService(string animeId, AnimeService service, int? season = null);

        /// <summary>
        /// Walks a list of external IDs (from a webhook payload) in priority order and returns
        /// the first one that resolves to a tracker id for <paramref name="service"/>. Tuples are
        /// <c>(prefix, raw id)</c> where <c>prefix</c> is one of <c>anidbPrefix</c>,
        /// <c>imdbPrefix</c>, <c>tmdbPrefix</c>, or <c>tvdbPrefix</c>. Returns null when no id
        /// resolves — typically because the title isn't anime or the mapping data has a gap.
        /// </summary>
        Task<string> ResolveExternalAsync(IEnumerable<(string prefix, string id)> externalIds, AnimeService service, int? season = null);

        /// <summary>
        /// Returns a sorted list of distinct season numbers available for the given anime ID.
        /// </summary>
        Task<List<int>> GetSeasonsAsync(string animeId);

        Task EnrichImdbMappings(List<AnimeIdMapping> mappings);
    }
}
