using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface IMalService
    {
        Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null, bool groupSeasons = true, string season = null, bool hideUnreleased = false);
        Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData, bool groupSeasons = true);

        /// <summary>
        /// Fetches just the title and total episode count for an anime — used by the
        /// Manage Entry season dropdown where we list every cour of a franchise.
        /// </summary>
        Task<(string? name, int? episodeCount)> GetAnimeSummaryAsync(string id);

        /// <summary>
        /// Fetches the legal-streaming destinations for an anime. MAL doesn't expose a
        /// proper streaming-link list; this returns an empty list and lets the caller
        /// fall back through the cross-service mapping if needed.
        /// </summary>
        Task<List<StreamingLink>> GetExternalLinksAsync(string animeId, TokenData tokenData);

        /// <summary>
        /// Fetches the user's list entry (status, progress) for a specific anime by its service-resolved ID and season.
        /// </summary>
        Task<AnimeEntry> GetAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null);

        /// <summary>
        /// Saves (creates or updates) the user's list entry. Any nullable parameter left null is
        /// left untouched server-side.
        /// </summary>
        Task SaveAnimeEntryAsync(TokenData tokenData, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null);

        /// <summary>
        /// Removes the anime from the user's list. No-op if it isn't there.
        /// </summary>
        Task DeleteAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null);

        /// <summary>
        /// Returns the user's entire MyAnimeList library with full <see cref="AnimeEntry"/>
        /// state attached, used by the manual full-sync flow. <see cref="AnimeEntry.MediaId"/>
        /// carries the <c>mal:</c> prefix so callers can hand it to the sync fan-out
        /// without re-prefixing. Status surfaces "rewatching" as a synthetic value when
        /// is_rewatching is set, mirroring <see cref="GetAnimeEntryAsync"/>.
        /// </summary>
        Task<List<AnimeEntry>> GetUserListEntriesAsync(TokenData tokenData);
    }
}
