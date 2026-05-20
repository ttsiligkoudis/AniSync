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
        /// Id resolution chain inside <c>BuildStremioId</c>:
        /// IMDb (<c>tt12345678:S:E</c>) → Kitsu (<c>kitsu:N:E</c>) →
        /// the caller's <paramref name="primaryService"/> id-space
        /// (<c>mal:N:E</c> / <c>anilist:N:E</c>). The primary fallback
        /// lets users on tracker-only entries (no IMDb / no Kitsu mapping
        /// in our local lists, e.g. very new shows) still get a request
        /// out to addons that index by their primary tracker.
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
            AnimeService primaryService,
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
    /// <see cref="IsHevc"/> flags HEVC / x265 / H.265 / Hi10P / 10-bit
    /// releases so the UI can warn on Chromium-desktop browsers where
    /// the hardware HEVC path produces visual corruption — detected
    /// from the addon's full name+description+title haystack because
    /// addons like MediaFusion put the codec line in <c>description</c>
    /// (which never makes the trip to the client otherwise).
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
        string Provider,
        bool IsHevc);
}
