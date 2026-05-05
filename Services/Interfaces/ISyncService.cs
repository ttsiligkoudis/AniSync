using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Orchestrates write fan-out from the primary provider to every linked secondary
    /// account. Reads stay on the primary; only mutations (Manage Entry save/delete,
    /// auto-track) are mirrored. Failures on a single linked provider don't fail the
    /// caller — sync is intentionally best-effort.
    /// </summary>
    public interface ISyncService
    {
        /// <summary>
        /// Mirrors a save against the linked providers attached to <paramref name="primary"/>.
        /// Status is translated to each target's vocabulary; score is normalised to a 0-10
        /// scale before hand-off. Fan-out is concurrent; per-target failures are swallowed.
        /// </summary>
        Task FanOutSaveAsync(TokenData primary, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null);

        /// <summary>
        /// Mirrors a delete against the linked providers attached to <paramref name="primary"/>.
        /// </summary>
        Task FanOutDeleteAsync(TokenData primary, string animeId, int? season);

        /// <summary>
        /// Returns every entry in the primary's library (status, progress, score, …) so the
        /// client-side full-sync flow can iterate them in batches. Dispatches to the right
        /// per-service GetUserListEntriesAsync based on <see cref="TokenData.anime_service"/>.
        /// </summary>
        Task<List<AnimeEntry>> GetPrimaryEntriesAsync(TokenData primary);
    }
}
