using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface ITokenService
    {
        Task<TokenData> GetAccessTokenAsync(string config = null);

        Task RemoveCachedUser();

        #region Anilist
        Task<TokenData> GetAccessTokenByCodeAsync(string code);
        #endregion

        #region Kitsu
        Task<TokenData> GetAccessTokenByCredsAsync(string username, string password, bool setContext = false, string userId = null);
        #endregion
    }
}

