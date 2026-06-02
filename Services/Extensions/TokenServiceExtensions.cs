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

        /// <summary>
        /// Resolves the token to use for ANIME operations. Trakt is a
        /// movies/series tracker with no anime id-space — the per-service anime
        /// dispatch (AniList / MAL / Kitsu) has no Trakt branch and
        /// <see cref="Utils.GetServicePrefix"/> throws for it. So when the
        /// primary is Trakt, anime runs on a linked anime provider instead:
        /// AniList first (anilist: ids + methods), then MAL, then Kitsu. With no
        /// anime provider linked, falls back to an anonymous AniList token so
        /// public anime catalogs / detail still resolve in the anilist: id-space.
        ///
        /// For an anime primary (or anonymous / null) the token is returned
        /// unchanged — this only re-points the Trakt-primary case. Movie/series
        /// flows must NOT call this (they keep the Trakt token).
        /// </summary>
        public static async Task<TokenData> ResolveAnimeTokenAsync(
            this IConfigStore configStore,
            TokenData primary)
        {
            if (primary == null || primary.anime_service != AnimeService.Trakt) return primary;

            var (uid, _) = await configStore.FindUidByIdentityAsync(primary);
            if (!string.IsNullOrEmpty(uid))
            {
                var linked = await configStore.GetLinkedTokensAsync(uid);
                var pick = PickLinkedAnime(linked, AnimeService.Anilist)
                        ?? PickLinkedAnime(linked, AnimeService.MyAnimeList)
                        ?? PickLinkedAnime(linked, AnimeService.Kitsu);
                if (pick != null) return pick;
            }

            // Nothing linked to read a real list from — anonymous AniList keeps
            // anime catalogs / detail working in the anilist: id-space.
            return new TokenData { anime_service = AnimeService.Anilist };
        }

        private static TokenData PickLinkedAnime(IEnumerable<LinkedToken> linked, AnimeService service) =>
            linked.FirstOrDefault(l => l.Service == service && !l.NeedsReauth && l.TokenData != null)?.TokenData;
    }
}
