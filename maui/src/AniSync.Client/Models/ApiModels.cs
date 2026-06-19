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

    /// <summary>AniList-overlaid airing timestamp (Unix seconds, UTC), when present.
    /// The detail page prefers this over <see cref="Released"/> for the "has it aired
    /// yet?" gate — matches the original Detail.cshtml's airingAt-first logic.</summary>
    public long? AiringAt { get; set; }
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

// ── Video (movie / series) detail (/api/v1/video/{type}/{id}) ─────────────────

/// <summary>One cast member for the video detail hero (video-cast-scroll). From
/// Trakt — <c>Image</c> is the headshot (null → the card shows the name initial);
/// <c>Slug</c> links to /discover/actor/{slug} (null → inert card).</summary>
public sealed class VideoCastDto
{
    public string? Name { get; set; }
    public string? Character { get; set; }
    public string? Image { get; set; }
    public string? Slug { get; set; }
}

/// <summary>Movie / series detail — the Trakt-enriched twin of <see cref="AnimeResponse"/>.
/// <c>Meta</c> carries the Cinemeta base overridden by Trakt (overview / runtime /
/// artwork / trailer); <c>Episodes</c> is the merged episode list (empty for movies);
/// <c>Recommended</c> is the Trakt /related poster row; <c>Cast</c> feeds the
/// video-cast-scroll; <c>Certification</c> rides the hero info line.</summary>
public sealed class VideoMetaResponse
{
    public MetaDto? Meta { get; set; }
    public List<VideoCastDto> Cast { get; set; } = new();
    public string? Certification { get; set; }
    public List<EpisodeInfoDto> Episodes { get; set; } = new();
    public List<MetaDto> Recommended { get; set; } = new();
    /// <summary>YouTube trailer id from Trakt's summary (null when none) — the
    /// video detail trailer card source.</summary>
    public string? TrailerYoutubeId { get; set; }
}

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

// ── Watch: episode streams / subtitles / mark-watched / scrobble ──────────────
// Mirror the server's EpisodeStreams* / EpisodeSubtitles* / MarkWatched /
// ScrobbleProgress / ResolveStream records (Models/Api/ApiResponses.cs). The
// /api/v1/me/episode-streams endpoint has two response shapes keyed on whether
// addonIndex was sent: the bootstrap (addons + external links + skip times) and
// the per-addon fan-out (enriched debrid rows).

/// <summary>Bootstrap response from GET /api/v1/me/episode-streams (no addonIndex):
/// the configured stream addons (for per-addon fan-out), external links, and
/// AniSkip markers.</summary>
public sealed class EpisodeStreamsBootstrap
{
    public bool Anonymous { get; set; }
    public bool AddonsConfigured { get; set; }
    public List<EpisodeStreamAddonDto> Addons { get; set; } = new();
    public List<EpisodeExternalLinkDto> ExternalLinks { get; set; } = new();
    public EpisodeSkipTimesDto? SkipTimes { get; set; }

    /// <summary>Not part of the live bootstrap response (the debrid rows fan out
    /// per-addon). Populated only when this record is reused as the watch page's
    /// localStorage cache carrier — the cached entry stores the combined
    /// { debridStreams, externalLinks, skipTimes } shape so a cache read can
    /// repaint the full source list without re-running the fan-out.</summary>
    public List<EpisodeStreamDto> DebridStreams { get; set; } = new();
}

public sealed class EpisodeStreamAddonDto { public int Index { get; set; } public string Name { get; set; } = ""; }
public sealed class EpisodeExternalLinkDto { public string? Site { get; set; } public string? Url { get; set; } }
public sealed class EpisodeSkipTimesDto { public EpisodeSkipMarkerDto? Intro { get; set; } public EpisodeSkipMarkerDto? Recap { get; set; } public EpisodeSkipMarkerDto? Outro { get; set; } }
public sealed class EpisodeSkipMarkerDto { public double Start { get; set; } public double End { get; set; } }

/// <summary>Per-addon fan-out response from GET /api/v1/me/episode-streams?addonIndex=N.</summary>
public sealed class EpisodeStreamsResponse { public List<EpisodeStreamDto> DebridStreams { get; set; } = new(); }

/// <summary>One enriched debrid stream row. <see cref="Url"/> is the resolved direct
/// file URL; <see cref="InfoHash"/> lets the client dedup identical releases across
/// addons before the per-resolution cap; <see cref="AudioUnsupported"/> /
/// <see cref="IsHevc"/> drive the watch page's "silent audio" / "may not play"
/// warnings.</summary>
public sealed class EpisodeStreamDto
{
    public string? Name { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? Quality { get; set; }
    public string? Size { get; set; }
    public bool Playable { get; set; }
    public int Seeders { get; set; }
    public string? Language { get; set; }
    public string? Provider { get; set; }
    public string? InfoHash { get; set; }
    public bool IsHevc { get; set; }
    public string? Source { get; set; }
    public string? Hdr { get; set; }
    public string? Audio { get; set; }
    public bool AudioUnsupported { get; set; }
    public string? Description { get; set; }
}

/// <summary>Subtitle lookup result from GET /api/v1/me/episode-subtitles.</summary>
public sealed class EpisodeSubtitlesResponse
{
    public List<EpisodeSubtitleDto> Subtitles { get; set; } = new();
    public EpisodeSubtitleProviderCounts? ProviderCounts { get; set; }
}

/// <summary>One subtitle track. <see cref="Url"/> is the upstream OpenSubtitles URL —
/// route it through <c>IAniSyncApi.SubtitleProxyUrl</c> for the same-origin SRT→VTT
/// conversion before handing it to a &lt;track&gt;.</summary>
public sealed class EpisodeSubtitleDto
{
    public string? Lang { get; set; }
    public string? Label { get; set; }
    public string? Url { get; set; }
    public string? Source { get; set; }
}

public sealed class EpisodeSubtitleProviderCounts { public int OpenSubtitles { get; set; } }

/// <summary>Result of POST /api/v1/me/mark-watched — <see cref="Reason"/> is a stable
/// opt-out / failure code or null on success.</summary>
public sealed class MarkWatchedResult { public bool Ok { get; set; } public string? Reason { get; set; } }

/// <summary>Result of POST /api/v1/me/scrobble-progress.</summary>
public sealed class ScrobbleProgressResult { public bool Ok { get; set; } public string? Reason { get; set; } }

/// <summary>Result of GET /api/v1/resolve-stream — the post-redirect debrid CDN URL.</summary>
public sealed class ResolveStreamResult { public string? ResolvedUrl { get; set; } }
