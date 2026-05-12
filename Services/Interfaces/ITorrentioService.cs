using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Proxies stream lookups to Torrentio (torrentio.strem.fun) — the
    /// public Stremio addon that searches torrent trackers, resolves
    /// matches through a debrid service, and returns playable direct
    /// URLs. AniSync forwards the user's Real-Debrid API key through
    /// the URL config segment Torrentio reads.
    ///
    /// v1 surfaces RD-cached streams only — entries whose
    /// <c>name</c> starts with <c>[RD+]</c> / <c>[RD download]</c> and
    /// whose <c>url</c> field is populated. Infohash-only entries
    /// (Torrentio would need to ask RD to cache them on demand) are
    /// dropped and tracked as a v1+1 follow-up.
    /// </summary>
    public interface ITorrentioService
    {
        /// <summary>
        /// Returns the page of RD-cached streams Torrentio knows about
        /// for the given anime + episode. Returns an empty list on any
        /// failure (key rejected, upstream 5xx, no ids resolvable,
        /// timeout) — never throws.
        /// </summary>
        Task<IReadOnlyList<TorrentioStream>> GetStreamsAsync(
            string apiKey,
            AnimeSourceLinks links,
            int? season,
            int? episode,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Slim projection of a Torrentio stream entry — just what the web
    /// modal + the Stremio addon's /stream endpoint need to render a
    /// pickable row. <see cref="Url"/> is the RD-resolved direct file
    /// URL; <see cref="Playable"/> tells the web side whether the
    /// browser can hand it to a &lt;video&gt; element (mp4/webm/m4v)
    /// or has to surface "open externally" affordances (mkv/avi).
    /// </summary>
    public record TorrentioStream(
        string Name,
        string Title,
        string Url,
        string Quality,
        string Size,
        bool Playable,
        string BingeGroup);
}
