
namespace AnimeList.Services.Interfaces
{
    public interface ICinemetaService
    {
        Task<string> GetAnimeByIdAsync(string config, string id, HttpRequest request = null);
    }
}
