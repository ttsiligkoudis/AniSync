
using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface IAnilistService
    {
        Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null);
        Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData);

        /// <summary>
        /// Fetches the user's list entry (status, progress) for a specific anime by its service-resolved ID and season.
        /// </summary>
        Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null);

        /// <summary>
        /// Saves (creates or updates) the user's list entry. Any nullable parameter left null is left
        /// untouched server-side (or, for new entries, takes the API's default).
        /// </summary>
        Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null);
    }
}

