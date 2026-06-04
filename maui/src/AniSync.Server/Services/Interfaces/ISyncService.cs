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
        /// scale before hand-off. Fan-out is concurrent; per-target failures don't throw,
        /// they're returned in <see cref="FanOutSaveResult.Failed"/> so foreground callers
        /// (Manage Entry save) can surface a partial-success warning. Background callers
        /// (scrobble webhook, sync backfill) discard the result and rely on the per-target
        /// log lines this method emits internally.
        /// </summary>
        Task<FanOutSaveResult> FanOutSaveAsync(TokenData primary, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null);

        /// <summary>
        /// Writes an episode-watched mark to the user's PRIMARY tracker (dispatching to
        /// AniList / MAL / Kitsu by <see cref="TokenData.anime_service"/>) and then fans
        /// out the same progress to every linked secondary. Single entry point so the
        /// "switch on anime_service → SaveAnimeEntryAsync → FanOutSaveAsync" recipe
        /// isn't duplicated between SubtitlesController (Stremio subtitle hook),
        /// AnimeController.MarkWatched (web-app 70 % / external-launch), and any
        /// future trigger.
        ///
        /// Honour-the-toggle is the caller's responsibility — this method always writes
        /// when invoked. Per-service primary-save failures propagate to the caller; fan-out
        /// failures stay best-effort the same way <see cref="FanOutSaveAsync"/> handles
        /// them.
        /// </summary>
        Task SaveProgressAndFanOutAsync(TokenData primary, string animeId, int? season, int episode);

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

        /// <summary>
        /// Reads the primary's current progress for a single entry. Returns null when the
        /// entry doesn't exist yet on the primary, or when the lookup fails. Used by
        /// auto-track entry points (e.g. the Stremio subtitle hook) to enforce a
        /// monotone-progress guard — a rewatch of an earlier episode shouldn't rewind the
        /// tracker to that earlier number.
        /// </summary>
        Task<int?> GetCurrentProgressAsync(TokenData primary, string animeId, int? season);
    }
}
