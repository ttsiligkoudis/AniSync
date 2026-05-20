using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services.Extensions
{
    /// <summary>
    /// Convenience extensions for <see cref="ITokenService"/> that bundle
    /// the most common "resolve the current session" patterns scattered
    /// across controllers (AnimeController, LibraryController,
    /// NotificationsController, etc.).
    /// </summary>
    public static class TokenServiceExtensions
    {
        /// <summary>
        /// Reads the current session's token and resolves its row UID via
        /// <see cref="IConfigStore.FindUidByIdentityAsync"/>. Returns
        /// <c>(token, null)</c> for anonymous sessions (the token is still
        /// surfaced so callers can read <c>anime_service</c>) and
        /// <c>(null, null)</c> when no session exists at all. Callers
        /// decide how to react — synthesize a Kitsu fallback, redirect,
        /// return 401, etc.
        /// </summary>
        public static async Task<(TokenData token, string uid)> ResolveCurrentAsync(
            this ITokenService tokenService,
            IConfigStore configStore)
        {
            var token = await tokenService.GetAccessTokenAsync();
            if (token == null || token.anonymousUser) return (token, null);
            var (uid, _) = await configStore.FindUidByIdentityAsync(token);
            return (token, uid);
        }
    }
}
