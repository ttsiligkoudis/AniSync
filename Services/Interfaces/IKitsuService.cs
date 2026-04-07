
using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface IKitsuService
    {
        Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null);
        Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData);

        /// <summary>
        /// Fetches the user's library entry (status, progress) for a specific anime.
        /// </summary>
        Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null);

        /// <summary>
        /// Saves (creates or updates) the user's library entry with the given status and progress.
        /// </summary>
        Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress, string status = null);
    }
}

