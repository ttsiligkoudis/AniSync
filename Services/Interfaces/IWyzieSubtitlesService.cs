using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Subtitle lookup against the Wyzie aggregator
    /// (https://sub.wyzie.ru). Wyzie federates Subdl, Addic7ed and
    /// other community sources behind a single IMDb-keyed search
    /// endpoint, so it tends to surface English anime fansubs that
    /// the OpenSubtitles addon doesn't carry. No API key — Wyzie is
    /// an open mirror. Sits alongside <see cref="ISubtitleService"/>;
    /// the controller calls both in parallel and merges the results.
    /// </summary>
    public interface IWyzieSubtitlesService
    {
        /// <summary>
        /// Returns the available Wyzie subtitle tracks for one
        /// episode. Empty when the show isn't indexed, the episode
        /// has no uploads, or any HTTP / parse failure occurs —
        /// best-effort. Results sourced from "OpenSubtitles" are
        /// filtered out at the service boundary to avoid duplicating
        /// the tracks already produced by <see cref="ISubtitleService"/>.
        /// </summary>
        Task<IReadOnlyList<SubtitleTrack>> SearchAsync(
            string imdbId,
            int? season,
            int episode,
            CancellationToken ct = default);
    }
}
