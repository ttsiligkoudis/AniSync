using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Anime-specialised subtitle lookup against the Jimaku community
    /// API (https://jimaku.cc/api). Keyed on AniList id — every cour
    /// has its own entry on Jimaku, so passing the per-cour AniList id
    /// avoids the "episode 3 of season 4 returns season 1 episode 3"
    /// trap. Sits alongside <see cref="ISubtitleService"/>; the
    /// controller calls both in parallel and merges the results so
    /// the player's caption menu picks up whichever provider has a
    /// match.
    /// </summary>
    public interface IJimakuSubtitlesService
    {
        /// <summary>
        /// Returns the available Jimaku subtitle tracks for one episode
        /// of an AniList entry. Empty when no API key is configured,
        /// the entry isn't tracked on Jimaku, the episode doesn't
        /// match any uploaded file, or any HTTP / parse failure
        /// occurs — best-effort.
        /// </summary>
        Task<IReadOnlyList<SubtitleTrack>> SearchAsync(
            int anilistId,
            int episode,
            string filename = null,
            CancellationToken ct = default);
    }
}
