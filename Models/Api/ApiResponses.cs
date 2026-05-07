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

    // ── User-scoped endpoints ────────────────────────────────────────────────

    /// <summary>Library export from the primary provider, optionally filtered by status.</summary>
    public record LibraryResponse(string Primary, string? Status, List<AnimeEntry> Entries);

    /// <summary>One library entry's full state.</summary>
    public record EntryResponse(AnimeEntry Entry);

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
