
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
    }
}
