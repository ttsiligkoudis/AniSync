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
    }
}
