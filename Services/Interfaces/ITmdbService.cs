using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface ITmdbService
    {
        /// <summary>
        /// Fetches anime metadata from the TMDB API by a tmdb-prefixed ID.
        /// </summary>
        Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData);

        /// <summary>Popular people (paginated) for the /discover/actors directory.</summary>
        Task<(List<ActorSummary> People, bool HasNextPage)> GetPopularPeopleAsync(int page = 1);
    }
}
