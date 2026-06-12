using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    public interface ITokenService
    {
        Task<TokenData> GetAccessTokenAsync(string config = null);

        Task RemoveCachedUser();

        /// <summary>
        /// Writes the user's UID to a persistent cookie so the next request after a
        /// process restart (fly.io redeploy, in-memory session store evicted, PWA
        /// reopen) can rehydrate the session from the SQLite config store instead
        /// of forcing the user to re-authenticate. Called by every login / primary-
        /// swap path; ignored for anonymous tokens that have no DB row.
        /// </summary>
        void SetPrimaryUidCookie(string uid);

        /// <summary>
        /// Clears the persistent UID cookie. Called by disconnect / logout / delete-
        /// configuration paths so the user actually stays logged out across restarts.
        /// </summary>
        void ClearPrimaryUidCookie();

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

        /// <summary>
        /// Same Kitsu password-grant sign-in as <see cref="GetAccessTokenByCredsAsync"/>, but
        /// returns the HTTP status and a human-readable failure reason instead of collapsing
        /// every cause (bad password, Cloudflare 403, rate-limit, network/TLS error) into a
        /// silent null. Used by the native (MAUI/TV) login endpoint so the UI can tell the
        /// user what actually went wrong.
        /// </summary>
        Task<KitsuLoginResult> GetAccessTokenByCredsDetailedAsync(string username, string password, bool setContext = false, string userId = null);
        #endregion

        #region MyAnimeList
        /// <summary>
        /// Exchanges a MyAnimeList authorization code (with the original PKCE verifier) for an
        /// access/refresh token pair. Setting <paramref name="setSession"/> false skips the
        /// session write — used by the link-provider flow.
        /// </summary>
        Task<TokenData> GetAccessTokenByMalCodeAsync(string code, string codeVerifier, bool setSession = true);
        #endregion

        /// <summary>
        /// Refreshes a linked secondary-provider token without touching the primary's session
        /// or in-memory token caches. Returns null when refresh fails — the caller should mark
        /// the linked token as needing re-auth and stop trying to use it.
        /// </summary>
        Task<TokenData> RefreshLinkedTokenAsync(TokenData token);
    }

    /// <summary>
    /// Outcome of a Kitsu password-grant attempt. <see cref="Token"/> is non-null only on
    /// success; on failure <see cref="Error"/> carries a user-facing reason and
    /// <see cref="StatusCode"/> is the Kitsu HTTP status (0 = the request never completed,
    /// e.g. a network/TLS failure).
    /// </summary>
    public sealed record KitsuLoginResult(TokenData Token, int StatusCode, string Error);
}

