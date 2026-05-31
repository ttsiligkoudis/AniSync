using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Http;

namespace AnimeList.Services
{
    /// <summary>
    /// Source of truth for the web UI's media-type modes (anime / movies /
    /// series). There are two related preferences:
    ///
    ///   • the ENABLED SET — the modes the user multi-selected in the chooser
    ///     modal. The dashboard combines shelves across every enabled mode, and
    ///     the Discover / Library toggles only offer these. Stored in a cookie
    ///     (<see cref="EnabledCookieName"/>) + localStorage, kept in sync by
    ///     media-type.js so SSR sees it for anonymous and logged-in visitors.
    ///
    ///   • the ACTIVE mode — the single mode Discover / Library currently render.
    ///     Stored in a cookie (<see cref="CookieName"/>, set client-side AND by
    ///     SetMediaType) and, for logged-in users, the durable account setting
    ///     (<see cref="IConfigStore.GetMediaTypeAsync"/>). Always clamped into
    ///     the enabled set.
    ///
    /// Resolution precedence for ACTIVE: logged-in account setting wins, else the
    /// cookie, else anime; then clamp to the enabled set. ENABLED falls back to
    /// the active mode as a singleton when its cookie is absent (pre-modal users).
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

        // Raw active (unclamped): logged-in setting, else cookie.
        private static MetaType ResolveRaw(HttpContext ctx, MetaType? storedForUser) =>
            storedForUser ?? FromCookie(ctx);

        /// <summary>
        /// The enabled set for a render. Logged-in / anonymous both read the
        /// cookie; when absent (pre-modal users) falls back to the active mode
        /// as a singleton so the dashboard still shows something. Never empty.
        /// </summary>
        public static async Task<List<MetaType>> ResolveEnabledAsync(HttpContext ctx, string uid, IConfigStore store)
        {
            var enabled = EnabledFromCookie(ctx);
            if (enabled.Count > 0) return enabled;
            return new() { await ResolveActiveAsync(ctx, uid, store) };
        }

        /// <summary>
        /// The single active mode for Discover / Library, clamped into the
        /// enabled set so the two preferences can't drift apart.
        /// </summary>
        public static async Task<MetaType> ResolveActiveAsync(HttpContext ctx, string uid, IConfigStore store)
        {
            MetaType? stored = string.IsNullOrEmpty(uid) ? null : await store.GetMediaTypeAsync(uid);
            var active = ResolveRaw(ctx, stored);
            var enabled = EnabledFromCookie(ctx);
            return enabled.Count == 0 || enabled.Contains(active) ? active : enabled[0];
        }
    }
}
