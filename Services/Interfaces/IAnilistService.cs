
using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface IAnilistService
    {
        Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null, string search = null, string sort = null, bool groupSeasons = true, string season = null, bool hideUnreleased = false);
        Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData, bool groupSeasons = true);

        /// <summary>
        /// Fetches just the title and total episode count for an anime — used by the
        /// Manage Entry season dropdown where we list every cour of a franchise. Avoids
        /// the heavy <see cref="GetAnimeByIdAsync"/> path so a 4-cour fan-out doesn't
        /// trigger AniList rate limits.
        /// </summary>
        Task<(string? name, int? episodeCount)> GetAnimeSummaryAsync(string id);

        /// <summary>
        /// Fetches the legal-streaming destinations for an anime (Crunchyroll, Netflix, …).
        /// </summary>
        Task<List<StreamingLink>> GetExternalLinksAsync(string animeId, TokenData tokenData);

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

        /// <summary>
        /// Removes the anime from the user's list. No-op if it isn't there. Used when the
        /// Manage Entry page is saved with the "None" status — the user is taking the anime
        /// off their list rather than updating it.
        /// </summary>
        Task DeleteAnimeEntryAsync(TokenData tokenData, string animeId, int? season = null);

        /// <summary>
        /// Returns the user's entire AniList library — every entry across every status — with
        /// full <see cref="AnimeEntry"/> state attached. Used by the manual full-sync flow to
        /// backfill linked providers after a fresh link. <see cref="AnimeEntry.MediaId"/> is
        /// returned with the <c>anilist:</c> prefix already attached so callers can pass it
        /// straight to the sync fan-out.
        /// </summary>
        Task<List<AnimeEntry>> GetUserListEntriesAsync(TokenData tokenData);

        /// <summary>
        /// Fetches the user's anime statistics (counts per status, mean score, total minutes
        /// watched, top genres) via AniList's <c>User.statistics</c> GraphQL — a single query
        /// that's vastly cheaper than fetching the Watching + Completed lists and computing
        /// these locally. Used by the dashboard "Your stats" panel when the user has an
        /// AniList token (primary or linked); MAL/Kitsu users see the panel only when they've
        /// linked an AniList account, because the other providers don't expose an equivalent.
        /// Returns null if the call fails or the token has no user_id attached.
        /// </summary>
        Task<AnilistUserStats?> GetUserStatsAsync(TokenData tokenData);
    }
}

