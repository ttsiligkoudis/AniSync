using AnimeList.Services.Interfaces;

namespace AnimeList.Models.Api
{
    // Response shapes for the public HTTP API. Records keep boilerplate minimal
    // while still giving Swashbuckle a concrete type to emit a schema for, so the
    // /api/docs page shows actual response models instead of a bare "OK".

    /// <summary>Standard error envelope returned by 4xx / 5xx responses.</summary>
    public record ApiError(string Error);

    // ── Public read-only endpoints ───────────────────────────────────────────

    /// <summary>Cross-service id mapping for a single service-prefixed id.</summary>
    public record MappingResponse(AnimeIdMapping Mapping);

    /// <summary>Cross-service id mappings for an IMDb / TMDB id (one per cour).</summary>
    public record MappingListResponse(List<AnimeIdMapping> Mappings);

    /// <summary>Unified anime detail. <c>Meta</c> is the provider-side record; the
    /// IMDb / Cinemeta path returns the raw Cinemeta meta JSON instead, which
    /// Swagger can't model without a passthrough type.</summary>
    public record AnimeResponse(Meta Meta);

    /// <summary>Recommendations for an anime, translated to the requested target service.</summary>
    public record SimilarResponse(List<Link> Similar);

    /// <summary>Streaming-destination links (Crunchyroll / Netflix / HiDive / …).</summary>
    public record StreamsResponse(List<StreamingLink> Streams);

    /// <summary>Search / discovery results.</summary>
    public record MetaListResponse(List<Meta> Results);

    /// <summary>Best-match resolver result (one row per match, ranked).</summary>
    public record MatchResponse(string Query, string Normalised, List<MatchResult> Matches);

    /// <summary>One match returned by <c>/match</c>.</summary>
    public record MatchResult(string? Id, string? Name, string? Poster, double Score, AnimeIdMapping? Mapping);

    /// <summary>AniSkip OP/ED/recap markers for one episode.</summary>
    public record SkipResponse(List<SkipTime> Markers);

    /// <summary>AnimeFillerList episode-number → category map.</summary>
    public record FillerResponse(string? Title, Dictionary<int, string> Categories);

    /// <summary>Today's airing schedule (UTC-day window). Same shape as the dashboard's "New Episodes Today" shelf.</summary>
    public record AiringTodayResponse(List<Meta> Items);

    /// <summary>Upcoming episodes in an explicit Unix-seconds window.</summary>
    public record AiringUpcomingResponse(long StartUnix, long EndUnix, List<UpcomingEpisode> Items);

    /// <summary>YouTube trailer id for an anime, or null when the show has no trailer or it's hosted elsewhere.</summary>
    public record TrailerResponse(string? YoutubeId);

    /// <summary>Supplementary chips for the detail page (tags, studios, staff, composer, …) sourced anonymously from AniList.</summary>
    public record SupplementaryResponse(List<Link> Links);

    /// <summary>Cross-service id bundle (AniList/MAL/Kitsu/IMDb/TMDB/TVDB/AniDB) for an anime, used by detail-page "open on X" buttons.</summary>
    public record SourceLinksResponse(AnimeSourceLinks Links);

    /// <summary>Current-season aggregate counts from AniList.</summary>
    public record SeasonStatsResponse(int CurrentlyAiring, int NewThisSeason, int TotalThisSeason);

    /// <summary>Full AniList tag catalog (non-adult, grouped by category), refreshed daily upstream.</summary>
    public record TagsListResponse(List<TagSummary> Tags);

    /// <summary>One page of AniList animation studios sorted by popularity.</summary>
    public record StudiosListResponse(List<StudioSummary> Studios, bool HasNextPage);

    /// <summary>One page of a studio's filmography.</summary>
    public record StudioMediaResponse(string? Name, List<Meta> Items, bool HasNextPage);

    /// <summary>A staff member's filmography, paginated via opaque <c>skip</c>.</summary>
    public record StaffMediaResponse(string? Name, List<Meta> Items);

    /// <summary>One page of anime tagged with a given tag.</summary>
    public record TagMediaResponse(string Tag, List<Meta> Items, bool HasNextPage);

    /// <summary>Subtitle tracks for one episode from OpenSubtitles.</summary>
    public record SubtitlesResponse(List<SubtitleTrack> Subtitles, SubtitleProviderCounts ProviderCounts);

    /// <summary>Per-provider count breakdown returned alongside <see cref="SubtitlesResponse"/>.</summary>
    public record SubtitleProviderCounts(int OpenSubtitles);

    /// <summary>Episode list extracted from an anime's full meta — same data the detail page renders, without the show envelope.</summary>
    public record EpisodesResponse(string AnimeId, List<EpisodeInfo> Episodes);

    /// <summary>One episode row in <see cref="EpisodesResponse"/>. <c>AiringAt</c> is
    /// the AniList-overlaid airing timestamp (Unix seconds, UTC) when present; the
    /// detail page's "has this episode aired yet?" gate prefers it over the Cinemeta
    /// <c>Released</c> string (AniList's community schedule leads Cinemeta's
    /// <c>released</c> by 1–2 days for some shows). Null when the cross-service
    /// mapping has no AniList schedule.</summary>
    public record EpisodeInfo(
        int Season,
        int Episode,
        string? Title,
        string? Thumbnail,
        string? Released,
        string? Overview,
        long? AiringAt = null);

    // ── User-scoped endpoints ────────────────────────────────────────────────

    /// <summary>Library export from the primary provider, optionally filtered by status.</summary>
    public record LibraryResponse(string Primary, string? Status, List<AnimeEntry> Entries);

    /// <summary>One library entry's full state.
    /// <para>For multi-cour franchises reached via a cross-service id (imdb:/tmdb:),
    /// <c>Seasons</c> carries one option per mapped cour, <c>SelectedEntryId</c> is the
    /// auto-resolved per-cour id the <c>Entry</c> was read against, and <c>Service</c> is
    /// the primary provider's <see cref="AnimeService"/> as an int (so the modal can pick
    /// the right score range + status vocabulary). All three are null/empty for native ids
    /// (anilist:/kitsu:/mal:) that don't need a season picker. Mirrors the MVC
    /// MetaController.GetEntryByApi shape.</para>
    /// <para><c>Entry</c> is null when the resolved cour isn't on the user's list yet —
    /// the response still carries <c>Seasons</c> so the modal can render the dropdown and
    /// land on the "None" status.</para></summary>
    public record EntryResponse(
        AnimeEntry Entry,
        List<EntrySeason> Seasons = null,
        int? Service = null,
        string SelectedEntryId = null);

    /// <summary>User's AniList statistics — counts, mean score, total hours watched. Requires an AniList token (primary or linked).</summary>
    public record UserStatsResponse(AnilistUserStats Stats);

    /// <summary>Continue-watching shelf — items from the user's <c>Watching</c> list capped at <paramref name="Items"/> length.</summary>
    public record ContinueWatchingResponse(string Primary, List<Meta> Items);

    /// <summary>Upcoming episodes airing in the next 24h that match the user's Watching list (same source the bell notifies from).</summary>
    public record UserUpcomingResponse(List<UserUpcomingEpisode> Items);

    /// <summary>One upcoming episode entry for <see cref="UserUpcomingResponse"/>.</summary>
    public record UserUpcomingEpisode(
        string AnimeId,
        string Title,
        int Episode,
        long AiringAt,
        string? CoverImage);

    /// <summary>Linked secondary providers attached to a config.</summary>
    public record LinkedResponse(string Primary, List<LinkedSummary> Linked);

    /// <summary>Compact linked-provider summary returned by <c>/linked</c>.</summary>
    public record LinkedSummary(string Service, bool NeedsReauth);

    // ── Writes ───────────────────────────────────────────────────────────────

    /// <summary>Save / delete result for a single entry.</summary>
    public record SaveEntryResponse(bool Ok, string Primary, bool? Removed);

    /// <summary>Bulk-save outcome — per-entry result rows + rolled-up counters.</summary>
    public record BulkSaveResponse(string Primary, int Ok, int Failed, int Total, List<BulkSaveResult> Results);

    /// <summary>One row of the bulk-save result list.</summary>
    public record BulkSaveResult(string? Id, bool Ok, bool? Removed, string? Error);

    /// <summary>Result of a primary-swap call.</summary>
    public record PromoteResponse(bool Ok, string? Primary, string? Reason);

    // ── Sync diff ────────────────────────────────────────────────────────────

    /// <summary>Diff between primary and each linked secondary.</summary>
    public record DiffResponse(string Primary, int PrimaryCount, List<DiffPerService> Diffs);

    /// <summary>Diff for one linked secondary.</summary>
    public record DiffPerService(
        string Service,
        bool? NeedsReauth,
        string? Error,
        int? MissingCount,
        int? MismatchedCount,
        List<DiffMissing>? Missing,
        List<DiffMismatched>? Mismatched);

    /// <summary>An entry on the primary that the secondary doesn't have.</summary>
    public record DiffMissing(DiffEntrySnapshot Primary);

    /// <summary>An entry whose primary and secondary state disagree.</summary>
    public record DiffMismatched(DiffEntrySnapshot Primary, DiffEntrySnapshot Secondary);

    /// <summary>Per-side snapshot fields surfaced by the diff.</summary>
    public record DiffEntrySnapshot(string MediaId, string? Status, int Progress);
}
