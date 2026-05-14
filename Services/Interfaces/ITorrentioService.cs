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

        /// <summary>
        /// Marks a torrent's <paramref name="infoHash"/> as "no longer
        /// playable" — Real-Debrid removed the file (typically DMCA)
        /// since Torrentio cached its instantAvailability snapshot.
        /// Hashes marked here are filtered out of subsequent
        /// <see cref="GetStreamsAsync"/> responses for a short window
        /// (~1h), so users stop seeing the dead-link row in the
        /// source picker. Idempotent and best-effort; passing
        /// null/empty/non-hash strings is a no-op. Persisted to the
        /// config store so the ban survives process restarts and is
        /// shared across app instances.
        /// </summary>
        Task MarkHashUnplayableAsync(string infoHash);
    }

    /// <summary>
    /// Slim projection of a debrid stream entry — just what the web
    /// modal + the Stremio addon's /stream endpoint need to render a
    /// pickable row. <see cref="Url"/> is the RD-resolved direct file
    /// URL; <see cref="Playable"/> tells the web side whether the
    /// browser can hand it to a &lt;video&gt; element (mp4/webm/m4v)
    /// or has to surface "open externally" affordances (mkv/avi).
    /// <see cref="Seeders"/> drives the per-resolution top-N ranking
    /// (higher seeders = more popular = more likely to be a clean
    /// release). <see cref="Language"/> is a best-effort extraction
    /// from the upstream title — flag emojis when present, else any
    /// MULTi / DUAL token; null when nothing recognisable was emitted.
    /// <see cref="Provider"/> identifies which addon emitted the entry
    /// ("Torrentio" / "MediaFusion") so the UI can render a small
    /// source badge and the merge step can de-dupe sensibly. Named
    /// <c>TorrentioStream</c> for historical reasons — kept stable to
    /// avoid a cross-cutting rename.
    /// </summary>
    public record TorrentioStream(
        string Name,
        string Title,
        string Url,
        string Quality,
        string Size,
        bool Playable,
        string BingeGroup,
        int Seeders,
        string Language,
        string InfoHash = null,
        int? FileIdx = null,
        string Provider = "Torrentio");
}
