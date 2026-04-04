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
        Task<AnimeIdMapping> GetImdbMapping(string imdb);
        Task<AnimeIdMapping> GetTmdbMapping(string tmdbId);
        Task<string> GetIdByService(string animeId, AnimeService service);
    }
}
