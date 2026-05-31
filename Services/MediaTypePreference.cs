using Microsoft.AspNetCore.Http;

namespace AnimeList.Services
{
    /// <summary>
    /// Source of truth for the web UI's media-type modes (anime / movies /
    /// series). Entirely COOKIE-BACKED — there is no per-user DB setting; the
    /// chooser modal writes localStorage + cookies and media-type.js keeps the
    /// cookies in sync, so server-side rendering honours the choice for anonymous
    /// and logged-in visitors alike.
    ///
    /// Two related preferences:
    ///   • ENABLED SET (<see cref="EnabledCookieName"/>) — the modes the user
    ///     multi-selected. The dashboard combines shelves across them; the
    ///     Discover / Library toggle only offers these.
    ///   • ACTIVE mode (<see cref="CookieName"/>) — the single mode Discover /
    ///     Library currently render. Always clamped into the enabled set.
    /// </summary>
    public static class MediaTypePreference
    {
        // Active single mode.
        public const string CookieName = "anisync_media_type";
        // Multi-selected enabled set (comma-separated mode names).
        public const string EnabledCookieName = "anisync_media_types";

        // Display order for the toggle chips + combined dashboard groups.
        private static readonly MetaType[] DisplayOrder =
            { MetaType.anime, MetaType.movie, MetaType.series };

        private static MetaType? Parse(string raw) => raw switch
        {
            "anime" => MetaType.anime,
            "movie" => MetaType.movie,
            "series" => MetaType.series,
            _ => null,
        };

        /// <summary>Parses the ACTIVE media-type cookie; anime when absent/unrecognised.</summary>
        public static MetaType FromCookie(HttpContext ctx) =>
            Parse(ctx?.Request?.Cookies[CookieName]) ?? MetaType.anime;

        /// <summary>True once the visitor has made (and persisted) a choice.</summary>
        public static bool HasChosen(HttpContext ctx) =>
            ctx?.Request?.Cookies?.ContainsKey(EnabledCookieName) == true;

        /// <summary>
        /// The enabled set from its cookie, de-duped + display-ordered. Empty
        /// when the cookie is absent (caller decides the fallback).
        /// </summary>
        public static List<MetaType> EnabledFromCookie(HttpContext ctx)
        {
            var raw = ctx?.Request?.Cookies[EnabledCookieName];
            if (string.IsNullOrEmpty(raw)) return new();
            var set = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Parse)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .ToHashSet();
            return DisplayOrder.Where(set.Contains).ToList();
        }

        /// <summary>
        /// Enabled set for the Discover / Library toggle chips. Cookie-based;
        /// always includes <paramref name="active"/> so the current surface's
        /// chip is offered even if the cookie is stale, and is never empty.
        /// </summary>
        public static List<MetaType> EnabledForToggle(HttpContext ctx, MetaType active)
        {
            var set = EnabledFromCookie(ctx).ToHashSet();
            set.Add(active);
            return DisplayOrder.Where(set.Contains).ToList();
        }

        /// <summary>
        /// The enabled set for a render. Falls back to the active mode as a
        /// singleton when the cookie is absent (pre-modal visitors) so the
        /// dashboard still shows something. Never empty.
        /// </summary>
        public static List<MetaType> ResolveEnabled(HttpContext ctx)
        {
            var enabled = EnabledFromCookie(ctx);
            return enabled.Count > 0 ? enabled : new() { ResolveActive(ctx) };
        }

        /// <summary>
        /// The single active mode for Discover / Library, clamped into the
        /// enabled set so the two preferences can't drift apart.
        /// </summary>
        public static MetaType ResolveActive(HttpContext ctx)
        {
            var active = FromCookie(ctx);
            var enabled = EnabledFromCookie(ctx);
            return enabled.Count == 0 || enabled.Contains(active) ? active : enabled[0];
        }
    }
}
