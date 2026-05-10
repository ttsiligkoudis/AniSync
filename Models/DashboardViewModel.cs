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

        // Stats panel. Watching/Completed totals come from list-fetch lengths;
        // TotalHoursWatched is sum(Completed episodes) × 24 min (the typical
        // TV-anime ep length); MeanScore averages user-rated Completed entries
        // (skipping unscored ones so the average isn't dragged down by zeros).
        // Phase 5 of the StreamD refactor surfaces the per-entry score +
        // episode count from each service's catalog query, which makes hours
        // and mean computable here without an extra round-trip per entry.
        // TopGenres is the top 5 genre buckets across Completed — the best
        // sample of long-run taste (Currently Watching skews to whatever
        // airing season is in flight).
        public int WatchingTotal { get; set; }
        public int CompletedTotal { get; set; }
        public int TotalHoursWatched { get; set; }
        public double? MeanScore { get; set; }
        public List<(string genre, int count)> TopGenres { get; set; } = [];

        // Names of the services that contributed data to the stats (primary
        // first, then any healthy linked secondaries). The view's "via X"
        // subtitle uses this to communicate "your stats span N accounts" when
        // there are multiple contributors. Empty for anonymous / not-logged-in
        // users, single-element for users with no linked accounts.
        public List<string> ContributingServices { get; set; } = [];

        // Seasonal aggregate counts surfaced on the dashboard's "This Season"
        // strip — same numbers regardless of the viewer's auth state, since
        // they describe the whole AniList catalog rather than the user's list.
        public int SeasonCurrentlyAiring { get; set; }
        public int SeasonNewThis { get; set; }
        public int SeasonTotal { get; set; }
    }
}
