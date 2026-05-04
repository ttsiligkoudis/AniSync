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

        #region MyAnimeList
        /// <summary>
        /// Exchanges a MyAnimeList authorization code (with the original PKCE verifier) for an
        /// access/refresh token pair. Persists the resulting <see cref="TokenData"/> to the
        /// session so the caller can return the user to the configure page.
        /// </summary>
        Task<TokenData> GetAccessTokenByMalCodeAsync(string code, string codeVerifier);
        #endregion
    }
}

