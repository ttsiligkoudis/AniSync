namespace AnimeList.Models
{
    /// <summary>
    /// Strongly-typed payload for the dashboard (Views/Home/Index.cshtml). Carries
    /// the session-derived token data (so the view can branch on login state and
    /// service), the resolved UID for per-card Manage Entry hand-offs, and the
    /// "Continue watching" slice — a small sample of the user's currently-watching
    /// list surfaced on the front door so the dashboard isn't just three nav tiles.
    /// </summary>
    public class DashboardViewModel
    {
        public TokenData TokenData { get; set; }
        public string ConfigUid { get; set; }
        public List<Meta> ContinueWatching { get; set; } = [];

        // Compact stats panel. Watching/Completed totals come straight from the
        // list-fetch lengths; TopGenres is the top 5 genre buckets across the
        // user's Completed list, which is the best sample of long-run taste
        // (Currently Watching skews to whatever airing season is in flight).
        public int WatchingTotal { get; set; }
        public int CompletedTotal { get; set; }
        public List<(string genre, int count)> TopGenres { get; set; } = [];
    }
}
