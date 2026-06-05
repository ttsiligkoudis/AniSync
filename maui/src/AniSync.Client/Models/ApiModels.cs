namespace AniSync.Client.Models;

// DTOs for the AniSync JSON API. Blazor's GetFromJsonAsync uses
// JsonSerializerDefaults.Web (case-insensitive), so PascalCase here binds to the
// server's camelCase wire shape without any attributes. Shapes mirror the
// records in Models/Api/ApiResponses.cs and the Meta model.

/// <summary>Unified card/detail shape (server-side AnimeList.Models.Meta).</summary>
public sealed class MetaDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Poster { get; set; }
    public string? Background { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }
    public string? Format { get; set; }
    public int? Episodes { get; set; }
    public int? AvgDuration { get; set; }
    public int? Year { get; set; }
    public string? Source { get; set; }
    public string? AirStatus { get; set; }
    public double? Score { get; set; }
    public int? Progress { get; set; }
    public string? EntryStatus { get; set; }
    public int? AiringEpisode { get; set; }
    public long? AiringAt { get; set; }
    public List<string>? Genres { get; set; }

    public bool IsAnime => string.Equals(Type, "anime", StringComparison.OrdinalIgnoreCase);
}

// ── Envelopes (match the ApiResponses records) ───────────────────────────────

public sealed class MetaListResponse { public List<MetaDto> Results { get; set; } = new(); }
public sealed class AnimeResponse { public MetaDto? Meta { get; set; } }
public sealed class AiringTodayResponse { public List<MetaDto> Items { get; set; } = new(); }
public sealed class ContinueWatchingResponse { public string? Primary { get; set; } public List<MetaDto> Items { get; set; } = new(); }
public sealed class StreamsResponse { public List<StreamingLinkDto> Streams { get; set; } = new(); }
public sealed class StreamingLinkDto { public string? Site { get; set; } public string? Url { get; set; } }
public sealed class EpisodesResponse { public string? AnimeId { get; set; } public List<EpisodeInfoDto> Episodes { get; set; } = new(); }

public sealed class EpisodeInfoDto
{
    public int Season { get; set; }
    public int Episode { get; set; }
    public string? Title { get; set; }
    public string? Thumbnail { get; set; }
    public string? Released { get; set; }
    public string? Overview { get; set; }
}

// ── Playback sources (Stremio addon: GET /{config}/stream/{type}/{id}.json) ──

public sealed class StremioStreamsResponse { public List<StremioStream> Streams { get; set; } = new(); }

/// <summary>One playable source. We only surface entries carrying a direct
/// <see cref="Url"/> (debrid links) — torrent-only (infoHash) rows can't play
/// in a thin client.</summary>
public sealed class StremioStream
{
    public string? Name { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }

    /// <summary>Display label — Name is the addon/quality tag, Title the file line.</summary>
    public string Label => string.Join(" · ",
        new[] { Name, Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

// ── Stats ({ success, stats } envelopes from /api/v1/me/stats + /Home/TraktStatsData) ──

public sealed class UserStatsResponse { public AnilistUserStatsDto? Stats { get; set; } }

public sealed class AnilistUserStatsDto
{
    public int Watching { get; set; }
    public int Completed { get; set; }
    public int TotalHoursWatched { get; set; }
    public double? MeanScore { get; set; }
}

public sealed class TraktStatsEnvelope { public bool Success { get; set; } public TraktUserStatsDto? Stats { get; set; } }

public sealed class TraktUserStatsDto
{
    public int MoviesWatched { get; set; }
    public int ShowsWatched { get; set; }
    public int EpisodesWatched { get; set; }
    public int TotalHoursWatched { get; set; }
}

// ── Search typeahead (/api/v1/suggest) ───────────────────────────────────────

public sealed class SuggestMatch
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Poster { get; set; }
    public string? Type { get; set; }
}

public sealed class SuggestResponse
{
    public string? Query { get; set; }
    public List<SuggestMatch> Matches { get; set; } = new();
}

// ── Best-match resolver (/api/v1/match) ──────────────────────────────────────

public sealed class MatchResult
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Poster { get; set; }
    public double Score { get; set; }
}

public sealed class MatchResponse
{
    public string? Query { get; set; }
    public string? Normalised { get; set; }
    public List<MatchResult> Matches { get; set; } = new();
}

// ── Detail-page extras (related/recommendations reuse MetaListResponse) ───────

/// <summary>Generic chip/link — supplementary credits, similar entries, source
/// chips. <c>AnilistId</c> lets the detail page route tag/studio/staff chips to
/// internal Discover routes; null falls back to the external <c>Url</c>.</summary>
public sealed class LinkDto
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Url { get; set; }
    public long? AnilistId { get; set; }
}

public sealed class SimilarResponse { public List<LinkDto> Similar { get; set; } = new(); }
public sealed class SupplementaryResponse { public List<LinkDto> Links { get; set; } = new(); }
public sealed class TrailerResponse { public string? YoutubeId { get; set; } }

/// <summary>Cross-service id bundle for the detail-page "open on X" chips.</summary>
public sealed class AnimeSourceLinksDto
{
    public int? AnilistId { get; set; }
    public int? MalId { get; set; }
    public int? KitsuId { get; set; }
    public string? ImdbId { get; set; }
    public int? ImdbSeason { get; set; }
}

public sealed class SourceLinksResponse { public AnimeSourceLinksDto? Links { get; set; } }

// ── AniSkip / filler (watch page) ────────────────────────────────────────────

/// <summary>One OP/ED/recap/preview marker. Times are seconds from file start.</summary>
public sealed class SkipMarker
{
    /// <summary>"op", "ed", "mixed-op", "mixed-ed", "recap", or "preview".</summary>
    public string? Type { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
}

public sealed class SkipResponse { public List<SkipMarker> Markers { get; set; } = new(); }
public sealed class FillerResponse { public string? Title { get; set; } public Dictionary<int, string> Categories { get; set; } = new(); }

// ── Discover browse-by (tags / studios / staff / actors) ──────────────────────

public sealed class TagSummaryDto
{
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public string? Description { get; set; }
}

public sealed class TagsListResponse { public List<TagSummaryDto> Tags { get; set; } = new(); }

public sealed class StudioSummaryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int AnimeCount { get; set; }
}

public sealed class StudiosListResponse { public List<StudioSummaryDto> Studios { get; set; } = new(); public bool HasNextPage { get; set; } }
public sealed class StudioMediaResponse { public string? Name { get; set; } public List<MetaDto> Items { get; set; } = new(); public bool HasNextPage { get; set; } }
public sealed class StaffMediaResponse { public string? Name { get; set; } public List<MetaDto> Items { get; set; } = new(); }
public sealed class TagMediaResponse { public string? Tag { get; set; } public List<MetaDto> Items { get; set; } = new(); public bool HasNextPage { get; set; } }

public sealed class ActorSummaryDto
{
    public int TmdbId { get; set; }
    public string Name { get; set; } = "";
    public string? Image { get; set; }
}

public sealed class ActorsListResponse { public List<ActorSummaryDto> Actors { get; set; } = new(); public bool HasNextPage { get; set; } }
public sealed class VideoModeDto { public string Slug { get; set; } = ""; public string Label { get; set; } = ""; }
public sealed class VideoModesResponse { public List<VideoModeDto> Modes { get; set; } = new(); }
public sealed class DashboardLayoutResponse { public string? Layout { get; set; } }
public sealed class ActorCreditsResponse
{
    public string? Slug { get; set; }
    public string? Name { get; set; }
    public string? Image { get; set; }
    public List<MetaDto> Movies { get; set; } = new();
    public List<MetaDto> Series { get; set; } = new();
}

// ── Season / catalog metadata ─────────────────────────────────────────────────

public sealed class SeasonStatsResponse { public int CurrentlyAiring { get; set; } public int NewThisSeason { get; set; } public int TotalThisSeason { get; set; } }
