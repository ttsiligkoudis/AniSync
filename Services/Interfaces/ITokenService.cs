using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface ITokenService
    {
        Task<TokenData> GetAccessTokenAsync(string config = null);

        Task RemoveCachedUser();

        #region Anilist
        /// <summary>
        /// Exchanges an AniList authorization code for an access/refresh token pair.
        /// Setting <paramref name="setSession"/> false skips writing the result to the
        /// session — used by the link-provider flow so a linked AniList account doesn't
        /// overwrite the primary login.
        /// </summary>
        Task<TokenData> GetAccessTokenByCodeAsync(string code, bool setSession = true);
        #endregion

        #region Kitsu
        Task<TokenData> GetAccessTokenByCredsAsync(string username, string password, bool setContext = false, string userId = null);
        #endregion

        #region MyAnimeList
        /// <summary>
        /// Exchanges a MyAnimeList authorization code (with the original PKCE verifier) for an
        /// access/refresh token pair. Setting <paramref name="setSession"/> false skips the
        /// session write — used by the link-provider flow.
        /// </summary>
        Task<TokenData> GetAccessTokenByMalCodeAsync(string code, string codeVerifier, bool setSession = true);
        #endregion
    }
}

