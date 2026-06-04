using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Fetches OP/ED/recap/preview timestamps from the AniSkip community API. Lookups
    /// are keyed on (MAL anime id, per-cour episode number); callers translate from
    /// whatever id Stremio handed them via <see cref="IAnimeMappingService"/>.
    /// </summary>
    public interface IAniSkipService
    {
        /// <summary>
        /// Returns the AniSkip markers for one episode, or an empty list when the API
        /// has no data, the call fails, or the upstream is down. Best-effort by design —
        /// every caller (Stream controller) treats a missing list as "skip the feature
        /// for this video" rather than as an error.
        /// </summary>
        Task<List<SkipTime>> GetSkipTimesAsync(int malId, int episode);
    }
}
