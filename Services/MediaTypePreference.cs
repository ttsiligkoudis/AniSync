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

        // Per-request override key. SetActiveCookie stamps the chosen mode here so
        // FromCookie reflects it immediately — the response cookie it writes isn't
        // visible on Request.Cookies until the next request.
        private const string ActiveOverrideKey = "AniSync.ActiveMediaType";

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

        /// <summary>
        /// Active media-type. An in-request override stamped by
        /// <see cref="SetActiveCookie"/> (a ?type= deep-link) wins so the switch UI
        /// matches the rendered content on first load; otherwise the active cookie,
        /// anime when absent/unrecognised.
        /// </summary>
        public static MetaType FromCookie(HttpContext ctx)
        {
            if (ctx?.Items != null
                && ctx.Items.TryGetValue(ActiveOverrideKey, out var ov)
                && ov is MetaType mt)
                return mt;
            return Parse(DecodeCookie(ctx?.Request?.Cookies[CookieName])) ?? MetaType.anime;
        }

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

        /// <summary>
        /// Parses a <c>?type=</c> query value ("anime" / "movie" / "series") into a
        /// <see cref="MetaType"/>; null when absent or unrecognised. Used by the
        /// Discover / Library / browse routes to honour a type-carrying deep-link
        /// (e.g. a dashboard "View all · Series" → <c>?type=series</c>).
        /// </summary>
        public static MetaType? ParseType(string raw) => Parse(raw);

        /// <summary>
        /// Persists <paramref name="type"/> as the active mode in the same cookie
        /// media-type.js writes (root path, 1y, Lax) so a type-carrying deep-link
        /// sticks across subsequent navigation on the surface — the active mode is
        /// a cookie-only view preference, so this is all SetMediaType does for it.
        /// </summary>
        public static void SetActiveCookie(HttpContext ctx, MetaType type)
        {
            if (ctx == null) return;
            ctx.Response.Cookies.Append(CookieName, type.ToString(), new CookieOptions
            {
                Path = "/",
                MaxAge = TimeSpan.FromDays(365),
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
            });
            // Make the choice visible to FromCookie within this same request
            // (Request.Cookies still holds the old value until the next request).
            if (ctx.Items != null) ctx.Items[ActiveOverrideKey] = type;
        }

        /// <summary>
        /// If <paramref name="type"/> is a valid mode, persists it as the active
        /// cookie and returns it; otherwise returns null (caller keeps the
        /// cookie-resolved active mode). One call covers the "honour ?type=, else
        /// respect the selected type" rule for Discover / Library / browse routes.
        /// </summary>
        public static MetaType? ApplyTypeQuery(HttpContext ctx, string type)
        {
            var parsed = Parse(type);
            if (parsed.HasValue) SetActiveCookie(ctx, parsed.Value);
            return parsed;
        }

        /// <summary>Enabled set for a render — reads the account setting for logged-in users.</summary>
        public static async Task<List<MetaType>> ResolveEnabledAsync(HttpContext ctx, string uid, IConfigStore store) =>
            ResolveEnabled(ctx, string.IsNullOrEmpty(uid) ? null : await store.GetWebSettingsAsync(uid));

        /// <summary>Active mode for a render — reads the account setting for logged-in users.</summary>
        public static async Task<MetaType> ResolveActiveAsync(HttpContext ctx, string uid, IConfigStore store) =>
            ResolveActive(ctx, await ResolveEnabledAsync(ctx, uid, store));
    }
}
