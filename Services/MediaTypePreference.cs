using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Http;

namespace AnimeList.Services
{
    /// <summary>
    /// Single source of truth for the web UI's media-type preference
    /// (anime / movies / series) used to drive server-side rendering across
    /// the dashboard, discover and meta surfaces.
    ///
    /// The preference is chosen from a first-visit modal and stored two ways:
    ///   • a non-HttpOnly cookie (<see cref="CookieName"/>) so the SERVER can
    ///     read it on every request — this is what makes SSR honour the choice
    ///     for ANONYMOUS visitors (localStorage isn't visible server-side); and
    ///   • the per-user <c>media_type</c> config row (logged-in users only) so
    ///     the choice is durable across devices.
    ///
    /// Resolution precedence: a logged-in user's stored setting wins (it's the
    /// cross-device truth the modal persists for them); anonymous visitors fall
    /// back to the cookie; anything unset defaults to anime — matching the
    /// historical "anonymous = anime" behaviour.
    /// </summary>
    public static class MediaTypePreference
    {
        public const string CookieName = "anisync_media_type";

        /// <summary>Parses the media-type cookie; anime when absent/unrecognised.</summary>
        public static MetaType FromCookie(HttpContext ctx)
        {
            var raw = ctx?.Request?.Cookies[CookieName];
            return raw switch
            {
                "movie" => MetaType.movie,
                "series" => MetaType.series,
                _ => MetaType.anime,
            };
        }

        /// <summary>True once the visitor has made (and persisted) a choice.</summary>
        public static bool HasChosen(HttpContext ctx) =>
            ctx?.Request?.Cookies?.ContainsKey(CookieName) == true;

        /// <summary>
        /// Resolves the effective media type for a render. Pass the logged-in
        /// user's stored setting (or null for anonymous); logged-in wins, else
        /// the anonymous cookie applies.
        /// </summary>
        public static MetaType Resolve(HttpContext ctx, MetaType? storedForUser) =>
            storedForUser ?? FromCookie(ctx);

        /// <summary>
        /// Convenience resolver for controllers holding a uid: reads the stored
        /// setting for logged-in users, otherwise the cookie. Anonymous uid
        /// (null/empty) never hits the store.
        /// </summary>
        public static async Task<MetaType> ResolveAsync(HttpContext ctx, string uid, IConfigStore store)
        {
            MetaType? stored = string.IsNullOrEmpty(uid)
                ? null
                : await store.GetMediaTypeAsync(uid);
            return Resolve(ctx, stored);
        }
    }
}
