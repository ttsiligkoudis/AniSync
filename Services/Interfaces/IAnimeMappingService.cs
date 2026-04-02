namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Resolves IMDb IDs from anime database IDs and vice versa using community-maintained mapping data.
    /// </summary>
    public interface IAnimeMappingService
    {
        Task EnsureLoadedAsync();
        Task<string> GetImdbIdByAnilistIdAsync(int anilistId);
        Task<string> GetImdbIdByKitsuIdAsync(int kitsuId);
        Task<int?> GetAnilistIdByImdbIdAsync(string imdbId);
        Task<int?> GetKitsuIdByImdbIdAsync(string imdbId);
        Task<int?> GetKitsuIdByAnilistIdAsync(int anilistId);
        Task<int?> GetAnilistIdByKitsuIdAsync(int kitsuId);
        Task<int?> GetIdByService(string animeId, AnimeService service);
    }
}
