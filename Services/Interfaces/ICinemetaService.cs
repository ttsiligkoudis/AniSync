
using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface ICinemetaService
    {
        Task<string> GetAnimeByIdAsync(string config, string id, HttpRequest request = null);

        /// <summary>
        /// Fetches Cinemeta's per-episode metadata (titles, thumbnails, synopses, air
        /// dates) for an IMDb-mapped show, optionally sliced to one cour. Used by the
        /// per-service GetAnimeByIdAsync paths so groupSeasons=false catalogs still get
        /// rich episode cards instead of the bare service-native data.
        /// </summary>
        /// <param name="imdbId">The IMDb tt-id of the show.</param>
        /// <param name="cinemetaSeason">When set, filters Cinemeta's flat video list to
        /// episodes from this season. When null, every episode is returned.</param>
        Task<List<Video>> GetEpisodesAsync(string imdbId, int? cinemetaSeason);

        /// <summary>
        /// Returns the slice of Cinemeta's videos that belongs to the cour identified by
        /// (<paramref name="service"/>, <paramref name="currentId"/>) inside the franchise
        /// behind <paramref name="imdbId"/>. Tries the Cinemeta-season filter first;
        /// falls back to cumulative-episode slicing using preceding cours' episode counts
        /// when the filter empties or the same season number repeats across mappings.
        ///
        /// <paramref name="getSummary"/> is the per-service
        /// <c>GetAnimeSummaryAsync(prefixed-id)</c> entry point — used to enrich missing
        /// episode counts on the IMDb mapping when computing the cumulative offset.
        /// </summary>
        Task<List<Video>> GetCourEpisodesAsync(
            string imdbId,
            int? cinemetaSeason,
            AnimeService service,
            int currentId,
            int currentEpisodeCount,
            Func<string, Task<(string? name, int? episodeCount)>> getSummary);
    }
}
