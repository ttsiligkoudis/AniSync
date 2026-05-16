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

        // Linked secondary providers attached to this config (e.g. AniList
        // primary + MAL + Kitsu linked). Names only — the dashboard's hero
        // "✓ Synced with X" badge renders them alongside the primary
        // service so the user sees every tracker their saves fan out to,
        // not just their primary. Empty when no secondaries are linked.
        public List<string> LinkedServices { get; set; } = [];

        // Stats panel — populated only when the viewer has an AniList token
        // (primary or linked), since the dashboard now reads stats from
        // AniList's User.statistics GraphQL rather than computing them
        // locally over the full Watching + Completed lists. MAL / Kitsu
        // primaries without an AniList link see the panel hidden (HasStats =
        // false); they can link AniList from /configure to unlock it.
        public bool HasStats { get; set; }
        public int WatchingTotal { get; set; }
        public int CompletedTotal { get; set; }
        public int TotalHoursWatched { get; set; }
        public double? MeanScore { get; set; }

        // Names of the services that contributed data to the stats. Stats
        // are AniList-only now, so this is either ["Anilist"] (panel shown)
        // or empty (panel hidden). Kept as a list to leave room for a future
        // multi-source aggregation without breaking the view contract.
        public List<string> ContributingServices { get; set; } = [];

        // Seasonal aggregate counts surfaced on the dashboard's "This Season"
        // strip — same numbers regardless of the viewer's auth state, since
        // they describe the whole AniList catalog rather than the user's list.
        public int SeasonCurrentlyAiring { get; set; }
        public int SeasonNewThis { get; set; }
        public int SeasonTotal { get; set; }

        // Top-15 popular slices for the current and next seasons, sorted by
        // AniList's POPULARITY_DESC. Same poster-grid Meta shape the
        // detail-page Recommended carousel uses, so the view can drop them
        // into the existing _PosterGrid scroll-row partial. Empty list means
        // the upstream blip'd or the season has no entries yet — view hides
        // the shelf in that case.
        public List<Meta> PopularThisSeason { get; set; } = [];
        public List<Meta> MostAnticipated { get; set; } = [];

        // Anime with at least one episode airing during today's UTC window.
        // One row per show (multi-cour drops collapse into a single card).
        // Cached server-side until the next UTC midnight so the shelf only
        // hits AniList once per calendar day.
        public List<Meta> NewEpisodesToday { get; set; } = [];
    }
}
