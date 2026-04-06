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
        Task<List<AnimeIdMapping>> GetImdbMapping(string imdb, int? season = null);
        Task<List<AnimeIdMapping>> GetTmdbMapping(string tmdbId, int? season = null);
        Task<string> GetIdByService(string animeId, AnimeService service, int? season = null);
    }
}
