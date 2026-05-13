using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Looks up subtitle tracks for an anime episode via the
    /// opensubtitles-v3 Stremio addon (opensubtitles-v3.strem.io) —
    /// the same source web.stremio.com queries by default. Returns
    /// proxied URLs (served by our own /anime/subtitle endpoint) so
    /// the &lt;track&gt; tag stays same-origin and the player can
    /// skip the CORS opt-in.
    /// </summary>
    public interface ISubtitleService
    {
        /// <summary>
        /// Returns the available subtitle tracks for the given anime
        /// episode. Looks up by IMDb id + season + episode (the
        /// addon's primary key) and, when provided, the source
        /// filename — the addon uses the filename to return
        /// release-matched subtitles whose timing actually matches
        /// the file the user picked. Anime entries without an IMDb
        /// mapping return an empty list. Best-effort: any HTTP /
        /// parse failure returns [] rather than surfacing the error
        /// to the caller.
        /// </summary>
        Task<IReadOnlyList<SubtitleTrack>> SearchAsync(
            string imdbId,
            int? season,
            int? episode,
            string filename = null,
            CancellationToken ct = default);

        /// <summary>
        /// Fetches the underlying SRT / VTT bytes for the given
        /// subtitle URL and returns them as a same-origin-safe
        /// text/vtt payload. Backs the /anime/subtitle proxy endpoint.
        /// Returns null on failure so the controller can 502 cleanly.
        /// </summary>
        Task<string> FetchAsVttAsync(string url, CancellationToken ct = default);
    }

    /// <summary>
    /// One subtitle track returned by the addon. Url is the *proxied*
    /// URL (served by /anime/subtitle), not the upstream URL — keeps
    /// the &lt;track&gt; load same-origin so the player can skip the
    /// CORS opt-in dance.
    /// </summary>
    public record SubtitleTrack(string Lang, string Label, string Url);
}
