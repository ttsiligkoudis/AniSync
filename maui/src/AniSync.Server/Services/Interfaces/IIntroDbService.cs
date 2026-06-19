using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Fetches intro / recap / outro timestamps for non-anime TV from the introdb.app
    /// community API (the series counterpart to <see cref="IAniSkipService"/>). Lookups
    /// are keyed on (IMDb id, season, episode). Best-effort: a missing key or any failure
    /// yields an empty list, so callers treat it as "no skip data for this episode".
    /// </summary>
    public interface IIntroDbService
    {
        /// <summary>
        /// Returns the introdb markers for one episode, normalised onto the AniSkip
        /// vocabulary ("op" / "recap" / "ed") so the bootstrap can map them the same way.
        /// Empty when no API key is configured, the API has no data, or the call fails.
        /// </summary>
        Task<List<SkipTime>> GetSkipTimesAsync(string imdbId, int season, int episode);
    }
}
