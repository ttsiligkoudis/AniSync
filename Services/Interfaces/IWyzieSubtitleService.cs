using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Looks up subtitle tracks for a given anime episode via Wyzie's
    /// public subtitle aggregator (sub.wyzie.ru) — the de-facto free
    /// subtitle source in the Stremio addon ecosystem. Returns proxied
    /// URLs (served by our own /anime/subtitle endpoint) so the
    /// &lt;track&gt; tag stays same-origin and the &lt;video&gt; element
    /// can skip the CORS opt-in.
    /// </summary>
    public interface IWyzieSubtitleService
    {
        /// <summary>
        /// Returns the available subtitle tracks for the given anime
        /// episode. Looks up by IMDb id (Wyzie's primary key) — anime
        /// entries without an IMDb mapping return an empty list.
        /// Best-effort: any HTTP / parse failure returns [] rather than
        /// surfacing the error to the caller.
        /// </summary>
        Task<IReadOnlyList<WyzieSubtitleTrack>> SearchAsync(
            string imdbId,
            int? season,
            int? episode,
            CancellationToken ct = default);

        /// <summary>
        /// Fetches the underlying SRT / VTT bytes for the given subtitle
        /// URL and returns them as a same-origin-safe text/vtt payload.
        /// Backs the /anime/subtitle proxy endpoint. Returns null on
        /// failure so the controller can 502 cleanly.
        /// </summary>
        Task<string> FetchAsVttAsync(string url, CancellationToken ct = default);
    }

    /// <summary>
    /// One subtitle track returned by Wyzie. Url is the *proxied* URL
    /// (served by /anime/subtitle), not the upstream Wyzie URL — keeps
    /// the &lt;track&gt; load same-origin so the player can skip the
    /// CORS opt-in dance.
    /// </summary>
    public record WyzieSubtitleTrack(string Lang, string Label, string Url);
}
