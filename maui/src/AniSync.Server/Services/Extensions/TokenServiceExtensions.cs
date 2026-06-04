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
        /// Resolves the token to use for per-entry ANIME operations (Manage
        /// Entry GET/save, the heart, the detail hero's entry state). Trakt is a
        /// movies/series tracker with no anime id-space, but its anime are
        /// actually tracked per-cour on the user's linked anime providers — so a
        /// Trakt primary routes these operations through the linked AniList
        /// (then MAL, then Kitsu) token. That keeps the modal's per-cour Season
        /// dropdown and writes the selected cour to those providers (the save's
        /// fan-out then mirrors it to the rest). With no anime provider linked,
        /// returns the Trakt token unchanged (the caller's video-Trakt path or
        /// the empty-list fallback handles it).
        ///
        /// IMPORTANT: this is for per-entry operations only — browsing / library
        /// / catalogs stay on Trakt + the IMDb id-space. For an anime primary
        /// (or anonymous / null) the token is returned unchanged.
        /// </summary>
        public static async Task<TokenData> ResolveAnimeTokenAsync(
            this IConfigStore configStore,
            TokenData primary)
        {
            if (primary == null || primary.anime_service != AnimeService.Trakt) return primary;

            var (uid, _) = await configStore.FindUidByIdentityAsync(primary);
            if (string.IsNullOrEmpty(uid)) return primary;

            var linked = await configStore.GetLinkedTokensAsync(uid);
            return PickLinkedAnime(linked, AnimeService.Anilist)
                ?? PickLinkedAnime(linked, AnimeService.MyAnimeList)
                ?? PickLinkedAnime(linked, AnimeService.Kitsu)
                ?? primary;
        }

        private static TokenData PickLinkedAnime(IEnumerable<LinkedToken> linked, AnimeService service) =>
            linked.FirstOrDefault(l => l.Service == service && !l.NeedsReauth && l.TokenData != null)?.TokenData;
    }
}
