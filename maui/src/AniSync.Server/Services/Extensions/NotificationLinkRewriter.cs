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
            AnimeService targetService,
            bool groupSeasons = false)
        {
            if (records == null || records.Count == 0) return;

            // Trakt isn't an anime id-space — there's nothing to translate anime
            // ids *to*, and GetServicePrefix(Trakt) throws. A Trakt-primary user's
            // notifications keep their stored links (already valid /meta/ deep-
            // links in whatever id-space they were minted with). Without this the
            // whole notifications page / bell list 500s for Trakt-primary users.
            if (targetService == AnimeService.Trakt) return;

            var targetPrefix = GetServicePrefix(targetService);
            foreach (var r in records)
            {
                if (string.IsNullOrEmpty(r?.AnimeId)) continue;

                // Trakt series notifications carry a bare IMDb id + a season-aware
                // /meta/{tt}/watch/{season}/{episode}?type=series link. The rewrites
                // below translate anime ids across AniList/MAL/Kitsu and rebuild the
                // path from the single-cour template — which would strip the season
                // and ?type=series. They don't apply to Trakt, so leave the link as
                // stored.
                if (r.Service == AnimeService.Trakt) continue;

                // Legacy rows were minted with the /anime/ prefix (pre-MetaController
                // consolidation); normalise to the unified /meta/ route so old + new
                // notifications both resolve. The id-space rewrites below already emit
                // /meta/ when they rebuild the path, so this only matters for the
                // "keep the stored link" short-circuits.
                if (!string.IsNullOrEmpty(r.LinkPath) && r.LinkPath.StartsWith("/anime/", StringComparison.Ordinal))
                    r.LinkPath = "/meta/" + r.LinkPath.Substring("/anime/".Length);

                // Group-seasons branch first — when on, every notification
                // should resolve to the imdb-grouped franchise umbrella,
                // even when the stored AnimeId already matches the user's
                // primary service. Falls back to the per-service rewrite
                // below when no imdb mapping exists (older / catalog-only
                // entries).
                if (groupSeasons)
                {
                    AnimeIdMapping rowMapping = null;
                    try
                    {
                        if (r.AnimeId.StartsWith(anilistPrefix, StringComparison.Ordinal))
                            rowMapping = await mapping.GetAnilistMapping(r.AnimeId);
                        else if (r.AnimeId.StartsWith(malPrefix, StringComparison.Ordinal))
                            rowMapping = await mapping.GetMalMapping(r.AnimeId);
                        else if (r.AnimeId.StartsWith(kitsuPrefix, StringComparison.Ordinal))
                            rowMapping = await mapping.GetKitsuMapping(r.AnimeId);
                    }
                    catch { rowMapping = null; }

                    if (rowMapping != null
                        && !string.IsNullOrEmpty(rowMapping.ImdbId)
                        && rowMapping.ImdbId.StartsWith("tt"))
                    {
                        // Embed the cour's IMDb season number when known so
                        // the multi-season Watch route lands on the right
                        // cour's episode. Falls back to the bare episode
                        // route for single-cour franchises (mapping.Season
                        // is null or 1).
                        var watchEp = r.EpisodeNumber;
                        var season = rowMapping.Season;
                        r.LinkPath = (season.HasValue && season.Value > 0)
                            ? $"/meta/{rowMapping.ImdbId}/watch/{season.Value}/{watchEp}"
                            : $"/meta/{rowMapping.ImdbId}/watch/{watchEp}";
                        r.AnimeId = rowMapping.ImdbId;
                        continue;
                    }
                }

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
                r.LinkPath = $"/meta/{translated}/watch/{r.EpisodeNumber}";
                r.AnimeId = translated;
            }
        }
    }
}
