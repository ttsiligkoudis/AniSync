
using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface IKitsuService
    {
        Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null);
        Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData);

        /// <summary>
        /// Fetches just the title and total episode count for an anime — used by the
        /// Manage Entry season dropdown where we list every cour of a franchise. Avoids
        /// the heavy <see cref="GetAnimeByIdAsync"/> path (which pulls categories,
        /// episodes, and AniList recommendations) so a multi-cour fan-out doesn't
        /// trigger Kitsu rate limits.
        /// </summary>
        Task<(string? name, int? episodeCount)> GetAnimeSummaryAsync(string id);

        /// <summary>
        /// Fetches the legal-streaming destinations for an anime.
        /// </summary>
        Task<List<StreamingLink>> GetExternalLinksAsync(string animeId, TokenData tokenData);

        /// <summary>
        /// Fetches the user's library entry (status, progress) for a specific anime.
        /// </summary>
        Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null);

        /// <summary>
        /// Saves (creates or updates) the user's library entry. Any nullable parameter left null is
        /// left untouched server-side (or, for new entries, takes the API's default).
        /// </summary>
        Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null);

        /// <summary>
        /// Removes the anime from the user's library. No-op if it isn't there. Used when the
        /// Manage Entry page is saved with the "None" status.
        /// </summary>
        Task DeleteAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null);

        /// <summary>
        /// Returns the user's entire Kitsu library with full <see cref="AnimeEntry"/> state
        /// attached, used by the manual full-sync flow. <see cref="AnimeEntry.MediaId"/>
        /// carries the <c>kitsu:</c> prefix so callers can hand it to the sync fan-out
        /// without re-prefixing.
        /// </summary>
        Task<List<AnimeEntry>> GetUserListEntriesAsync(TokenData tokenData);
    }
}

