namespace AnimeList.Models
{
    /// <summary>
    /// Strongly-typed payload for the dashboard (Views/Home/Index.cshtml). Carries
    /// the session-derived token data (so the view can branch on login state and
    /// service) and the resolved UID for per-card Manage Entry hand-offs. The
    /// "Continue watching" shelf is fetched client-side from
    /// <c>/Home/ContinueWatchingData</c> with a localStorage cache in front
    /// — it doesn't live on this model anymore.
    /// </summary>
    public class DashboardViewModel
    {
        public TokenData TokenData { get; set; }
        public string ConfigUid { get; set; }

        // Linked secondary providers attached to this config (e.g. AniList
        // primary + MAL + Kitsu linked). Names only — the dashboard's hero
        // "✓ Synced with X" badge renders them alongside the primary
        // service so the user sees every tracker their saves fan out to,
        // not just their primary. Empty when no secondaries are linked.
        public List<string> LinkedServices { get; set; } = [];

        // Stats panel gate — true when the viewer has an AniList token
        // (primary or linked) we can fetch from. The actual numbers don't
        // live on this model anymore: the view renders skeleton "—"
        // placeholders and the dashboard JS hits /Home/AnilistStats from
        // the client (with a 24 h localStorage cache in front), so each
        // dashboard render doesn't pay the AniList round-trip. MAL / Kitsu
        // primaries without an AniList link see the panel hidden; they
        // can link AniList from /configure to unlock it.
        public bool HasStats { get; set; }

        // True when the viewer has ≥1 stream addon configured. Gates the
        // "set up streaming" nudge banner the dashboard renders for
        // logged-in non-anonymous users — shown only when this is false,
        // so it self-dismisses the moment the user adds an addon on
        // /advanced (no client round-trip needed; the next dashboard
        // render just stops emitting the banner).
        public bool HasStreamAddons { get; set; }

        // Names of the services that contributed data to the stats. Stats
        // are AniList-only now, so this is either ["Anilist"] (panel shown)
        // or empty (panel hidden). Kept as a list to leave room for a future
        // multi-source aggregation without breaking the view contract.
        public List<string> ContributingServices { get; set; } = [];

        // The "This Season" stat strip and the discovery shelves (New
        // Episodes Today, Most Popular this Season, Most Anticipated) are no
        // longer rendered server-side — they're fetched client-side after
        // first paint from /Home/SeasonStatsData and the *Data shelf
        // endpoints, so the dashboard never blocks on AniList. The view
        // emits shimmer placeholders and the inline loader swaps them for
        // the real content (or hides the section when the upstream returns
        // nothing).
    }
}
