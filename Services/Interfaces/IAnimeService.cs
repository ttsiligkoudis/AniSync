using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface IAnimeService
    {
        Task<List<Meta>> GetAnimeListAsync(TokenData tokenData, ListType? list = null, string skip = null, string animeId = null, string genre = null);
        Task<Meta> GetAnimeByIdAsync(string id, TokenData tokenData);
        Task UpdateEpisodeProgressAsync(TokenData tokenData, string animeId, int season, int episode);
    }
}

