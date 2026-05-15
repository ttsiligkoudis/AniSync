using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Generic Stremio-addon stream consumer. Replaces the per-provider
    /// TorrentioService / MediaFusionService split — every Stremio stream
    /// addon exposes the same protocol (manifest.json + /stream/{type}/{id}.json
    /// with a standard <c>{ streams: [...] }</c> response shape), so one
    /// fetcher covers Torrentio, MediaFusion, Comet, Jackettio, AIOStreams,
    /// and anything else that conforms.
    /// </summary>
    public interface IAddonStreamService
    {
        /// <summary>
        /// Fetches the addon's manifest, validates that it advertises
        /// stream support, and returns a <see cref="StreamAddon"/> with
        /// its display name. Returns null when the URL doesn't parse,
        /// the addon doesn't respond, or the manifest doesn't declare
        /// <c>stream</c> as one of its resources. Called once per
        /// add-button click on the Configure page.
        /// </summary>
        Task<StreamAddon> FetchManifestAsync(string manifestUrl, CancellationToken ct = default);

        /// <summary>
        /// Pulls the addon's stream list for one (anime, season, episode).
        /// Strips <c>/manifest.json</c> from <paramref name="manifestUrl"/>,
        /// appends <c>/stream/{type}/{id}.json</c>, parses the response
        /// using the de-facto Torrentio token conventions (💾 size, 👤
        /// seeders, flag emojis for language, "1080p"/"720p" in the
        /// title/description). Drops entries that aren't directly
        /// playable (no <c>url</c> field — magnets-only addons,
        /// external-only addons). Returns an empty list on any failure.
        ///
        /// <paramref name="clientIp"/> is forwarded as the
        /// <c>X-Forwarded-For</c> / <c>Fly-Client-IP</c> /
        /// <c>CF-Connecting-IP</c> headers so addons that bind their
        /// playback tokens to the requesting IP (notably MediaFusion's
        /// ElfHosted instance) sign URLs to the user's IP rather than
        /// AniSync's backend IP — without it, the user's browser hits
        /// the addon's playback URL from a different IP than the one
        /// the token was issued for and the addon 403s.
        /// </summary>
        Task<IReadOnlyList<AddonStream>> GetStreamsAsync(
            string manifestUrl,
            AnimeSourceLinks links,
            int? season,
            int? episode,
            string clientIp = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Slim projection of a Stremio stream entry — just what the web
    /// modal + the Stremio addon's /stream endpoint need to render a
    /// pickable row. <see cref="Url"/> is the addon's resolved direct
    /// file URL; <see cref="Playable"/> tells the web side whether the
    /// browser can hand it to a &lt;video&gt; element (mp4/webm/m4v)
    /// or has to surface "open externally" affordances (mkv/avi).
    /// <see cref="Seeders"/> drives the per-resolution top-N ranking
    /// (higher seeders = more popular = more likely to be a clean
    /// release). <see cref="Language"/> is a best-effort extraction
    /// from the addon's title — flag emojis when present, else any
    /// MULTi / DUAL token; null when nothing recognisable was emitted.
    /// <see cref="Provider"/> is the addon's display name, used as a
    /// source badge in the UI and as a discriminator in the merge step.
    /// </summary>
    public record AddonStream(
        string Name,
        string Title,
        string Url,
        string Quality,
        string Size,
        bool Playable,
        string BingeGroup,
        int Seeders,
        string Language,
        string InfoHash,
        int? FileIdx,
        string Provider);
}
