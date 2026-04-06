
using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface IAnilistService
    {
        Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null);
        Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData);
        Task UpdateEpisodeProgressAsync(TokenData tokenData, string animeId, int season, int episode);

        /// <summary>
        /// Fetches the user's list entry (status, progress) for a specific anime by its service-resolved ID and season.
        /// </summary>
        Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null);

        /// <summary>
        /// Saves (creates or updates) the user's list entry with the given status and progress.
        /// </summary>
        Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, string status, int progress);
    }
}

