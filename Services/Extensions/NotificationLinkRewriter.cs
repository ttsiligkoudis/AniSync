using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services.Extensions
{
    /// <summary>
    /// Rewrites notification deep-links so a click on the bell row lands
    /// on the user's CURRENT primary service's id-space rather than the
    /// id-space the row was minted with. Without this a user who flipped
    /// primary provider after the notification was created (anilist → mal)
    /// would land on <c>/anime/anilist:N/watch/E</c> even though their
    /// /anime/{id} routes are now resolving against MAL — Manage Entry
    /// would mis-target and the cross-service navigation feels stale.
    /// Falls back to the stored link when no cross-service mapping exists
    /// for the target service.
    /// </summary>
    public static class NotificationLinkRewriter
    {
        /// <summary>
        /// In-place translation of every <see cref="NotificationRecord.LinkPath"/>
        /// (and <see cref="NotificationRecord.AnimeId"/>) to <paramref name="targetService"/>'s
        /// id-space. The DB row stays as-is — only the in-memory copy
        /// being shipped to the client is mutated. Rows whose stored id
        /// already matches the target service short-circuit without a
        /// mapping lookup.
        /// </summary>
        public static async Task RewriteLinksToServiceAsync(
            this IAnimeMappingService mapping,
            IReadOnlyCollection<NotificationRecord> records,
            AnimeService targetService)
        {
            if (records == null || records.Count == 0) return;
            var targetPrefix = GetServicePrefix(targetService);
            foreach (var r in records)
            {
                if (string.IsNullOrEmpty(r?.AnimeId)) continue;
                if (r.AnimeId.StartsWith(targetPrefix, StringComparison.Ordinal)) continue;

                // GetIdWithPrefixAsync returns null when the row's source
                // service has no entry in the cross-service mapping for
                // the target — keep the stored link in that case so the
                // user still gets a working (if not ideally-targeted)
                // deep-link. Swallow lookup exceptions for the same
                // reason: a transient mapping-load hiccup mustn't break
                // the bell render.
                string translated;
                try { translated = await mapping.GetIdWithPrefixAsync(r.AnimeId, targetService); }
                catch { continue; }
                if (string.IsNullOrEmpty(translated)) continue;
                if (string.Equals(translated, r.AnimeId, StringComparison.Ordinal)) continue;

                // Stored LinkPath is always /anime/{stored-id}/watch/{ep}
                // (set by EpisodeNotificationDispatcher); rebuild from
                // the canonical format rather than string-replacing the
                // old id, which would silently break if the dispatcher
                // ever evolves the URL shape.
                r.LinkPath = $"/anime/{translated}/watch/{r.EpisodeNumber}";
                r.AnimeId = translated;
            }
        }
    }
}
