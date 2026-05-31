using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Http;

namespace AnimeList.Services
{
    /// <summary>
    /// Source of truth for the web UI's media-type modes (anime / movies /
    /// series). Two related preferences:
    ///   • ENABLED SET — the modes the user multi-selected in the chooser modal.
    ///     The dashboard combines shelves across them; the Discover / Library
    ///     toggle only offers these.
    ///   • ACTIVE mode — the single mode Discover / Library currently render.
    ///     Always clamped into the enabled set.
    ///
    /// Persistence: for LOGGED-IN users the account setting (DB
    /// <see cref="IConfigStore.GetWebSettingsAsync"/>) is the source of truth so
    /// the choice follows them across devices; anonymous visitors fall back to a
    /// cookie (kept in sync by media-type.js, so SSR honours their choice too).
    /// The ACTIVE mode is a transient view preference and stays cookie-only.
    /// </summary>
    public static class MediaTypePreference
    {
        public const string CookieName = "anisync_media_type";        // active single
        public const string EnabledCookieName = "anisync_media_types"; // enabled set (csv)

        private static readonly MetaType[] DisplayOrder =
            { MetaType.anime, MetaType.movie, MetaType.series };

        private static MetaType? Parse(string raw) => raw switch
        {
            "anime" => MetaType.anime,
            "movie" => MetaType.movie,
            "series" => MetaType.series,
            _ => null,
        };

        // Parses + de-dupes + display-orders a comma-separated mode list.
        private static List<MetaType> ParseCsv(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return new();
            var set = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Parse)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .ToHashSet();
            return DisplayOrder.Where(set.Contains).ToList();
        }

        // Request cookies are written URL-encoded by media-type.js (commas would
        // otherwise be read as a cookie separator), so decode on the way in.
        private static string DecodeCookie(string raw) =>
            string.IsNullOrEmpty(raw) ? raw : Uri.UnescapeDataString(raw);

        /// <summary>Active media-type cookie; anime when absent/unrecognised.</summary>
        public static MetaType FromCookie(HttpContext ctx) =>
            Parse(DecodeCookie(ctx?.Request?.Cookies[CookieName])) ?? MetaType.anime;

        /// <summary>Enabled set from its cookie, de-duped + display-ordered. Empty when absent.</summary>
        public static List<MetaType> EnabledFromCookie(HttpContext ctx) =>
            ParseCsv(DecodeCookie(ctx?.Request?.Cookies[EnabledCookieName]));

        /// <summary>
        /// Enabled set for the Discover / Library toggle chips. Always includes
        /// <paramref name="active"/> so the current surface's chip is offered,
        /// and is never empty.
        /// </summary>
        public static List<MetaType> ForToggle(IEnumerable<MetaType> enabled, MetaType active)
        {
            var set = (enabled ?? Enumerable.Empty<MetaType>()).ToHashSet();
            set.Add(active);
            return DisplayOrder.Where(set.Contains).ToList();
        }

        /// <summary>
        /// The enabled set, given an already-fetched <see cref="WebSettings"/>
        /// (null for anonymous). Account setting wins; else the cookie; else the
        /// active mode as a singleton (pre-modal users) so a dashboard still
        /// shows something. Never empty.
        /// </summary>
        public static List<MetaType> ResolveEnabled(HttpContext ctx, WebSettings ws)
        {
            var fromDb = ParseCsv(ws?.EnabledMediaTypes);
            if (fromDb.Count > 0) return fromDb;
            var cookie = EnabledFromCookie(ctx);
            return cookie.Count > 0 ? cookie : new() { FromCookie(ctx) };
        }

        /// <summary>The active mode clamped into the supplied enabled set.</summary>
        public static MetaType ResolveActive(HttpContext ctx, List<MetaType> enabled)
        {
            var active = FromCookie(ctx);
            return enabled.Contains(active) ? active : enabled[0];
        }

        /// <summary>Enabled set for a render — reads the account setting for logged-in users.</summary>
        public static async Task<List<MetaType>> ResolveEnabledAsync(HttpContext ctx, string uid, IConfigStore store) =>
            ResolveEnabled(ctx, string.IsNullOrEmpty(uid) ? null : await store.GetWebSettingsAsync(uid));

        /// <summary>Active mode for a render — reads the account setting for logged-in users.</summary>
        public static async Task<MetaType> ResolveActiveAsync(HttpContext ctx, string uid, IConfigStore store) =>
            ResolveActive(ctx, await ResolveEnabledAsync(ctx, uid, store));
    }
}
