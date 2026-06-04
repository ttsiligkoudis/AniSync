namespace AnimeList.Models
{
    /// <summary>
    /// Compact projection of Trakt's <c>/users/me/stats</c> response used by the
    /// video-mode dashboard "Your stats" strip — the Trakt counterpart to the
    /// anime dashboard's AniList stats. Serialised camelCase to the client by
    /// <c>/Home/TraktStatsData</c>.
    /// </summary>
    public class TraktUserStats
    {
        public int MoviesWatched { get; set; }
        public int ShowsWatched { get; set; }
        public int EpisodesWatched { get; set; }
        // Movie + episode watch minutes, rolled up to whole hours.
        public int TotalHoursWatched { get; set; }
    }
}
