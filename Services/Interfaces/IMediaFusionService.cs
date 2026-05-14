using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Proxies stream lookups to MediaFusion — a Stremio addon that
    /// aggregates many anime-relevant indexers (Nyaa.si, AniDex,
    /// ElfCache, Prowlarr-fed sources) and resolves matches through
    /// the user's debrid provider. Adds significant anime coverage
    /// on top of Torrentio because MediaFusion indexes Nyaa directly.
    ///
    /// AniSync doesn't generate the MediaFusion URL — its config
    /// segment is encrypted server-side at MF, so the user pastes
    /// their personal manifest URL into the Configure page and we
    /// store it as a flat string. The service strips
    /// <c>/manifest.json</c> from that URL to derive the addon root,
    /// then appends Stremio's standard <c>/stream/{type}/{id}.json</c>.
    /// </summary>
    public interface IMediaFusionService
    {
        /// <summary>
        /// Returns the page of debrid-cached streams MediaFusion knows
        /// about for the given anime + episode. Returns an empty list
        /// on any failure (bad URL, upstream 5xx, no ids resolvable,
        /// timeout) — never throws.
        /// </summary>
        Task<IReadOnlyList<TorrentioStream>> GetStreamsAsync(
            string manifestUrl,
            AnimeSourceLinks links,
            int? season,
            int? episode,
            CancellationToken ct = default);
    }
}
