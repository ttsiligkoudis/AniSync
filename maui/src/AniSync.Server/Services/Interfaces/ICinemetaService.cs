
using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface ICinemetaService
    {
        Task<string> GetAnimeByIdAsync(string config, string id, HttpRequest request = null);

        /// <summary>
        /// Parses Cinemeta's meta JSON into an in-app <see cref="Meta"/> with
        /// every cour's episodes attached (season numbers preserved). Used
        /// by the imdb-grouped detail-page render: instead of fetching one
        /// cour's data from AniList/MAL/Kitsu and stitching the franchise
        /// video list on top, we treat Cinemeta as the source of truth for
        /// title/synopsis/poster/episodes — mirrors how Stremio renders an
        /// imdb-grouped catalog entry on its own. Returns null when
        /// Cinemeta has no entry for the id.
        /// </summary>
        Task<Meta> GetMetaAsync(string imdbId);

        /// <summary>
        /// Fetches one of Cinemeta's discovery catalogs (the "top" popularity
        /// list) for the video section's browse pages. Unlike
        /// <see cref="GetMetaAsync"/> this does NOT gate on the anime IMDb-
        /// mapping table — it talks to Cinemeta's <c>/catalog</c> endpoint
        /// directly so general movies / series (not just anime) resolve.
        /// </summary>
        /// <param name="type">Cinemeta content type — "movie" or "series".</param>
        /// <param name="genre">Optional genre filter (e.g. "Action"); null for all.</param>
        /// <param name="search">Optional search query; when set, returns the
        /// relevance-ranked search results instead of the popularity list.</param>
        /// <param name="skip">Item offset for pagination (Cinemeta pages in
        /// blocks of 100). 0 for the first page.</param>
        Task<List<Meta>> GetVideoCatalogAsync(string type, string genre = null, string search = null, int skip = 0);

        /// <summary>
        /// Fetches full Cinemeta meta (title / poster / background / genres /
        /// episodes) for a single title by (type, IMDb id), bypassing the
        /// anime-mapping gate in <see cref="GetMetaAsync"/>. Backs the video
        /// section's detail + watch pages. Returns null when Cinemeta has no
        /// entry for the id.
        /// </summary>
        Task<Meta> GetVideoMetaAsync(string type, string imdbId);

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
