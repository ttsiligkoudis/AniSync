namespace AniSync.Client.Models;

// DTOs for the tracking / watch / notifications / calendar surfaces. Web JSON
// (case-insensitive) binding, so PascalCase maps to the camelCase wire shape.

// ── Library entry (manage-entry modal) ───────────────────────────────────────

public sealed class AnimeEntryDto
{
    public string? EntryId { get; set; }
    public string? MediaId { get; set; }
    /// <summary>Service-specific status string from the tracker (e.g. CURRENT, watching).</summary>
    public string? Status { get; set; }
    public int Progress { get; set; }
    public int? TotalEpisodes { get; set; }
    public double? Score { get; set; }
    public string? Notes { get; set; }
    public int RewatchCount { get; set; }
}

public sealed class EntryResponse { public AnimeEntryDto? Entry { get; set; } }

/// <summary>POST body for /api/v1/me/entries/{id}. Status is the canonical
/// ListStatus name (Watching/Completed/Planning/Paused/Dropped/Rewatching);
/// the server (JsonStringEnumConverter is registered) parses the string.</summary>
public sealed class EntrySaveRequest
{
    public string? Status { get; set; }
    public int? Progress { get; set; }
    public double? Score { get; set; }
}

public sealed class SaveEntryResponse { public bool Ok { get; set; } public string? Primary { get; set; } public bool? Removed { get; set; } }

// ── Notifications ────────────────────────────────────────────────────────────

public sealed class NotificationDto
{
    public long Id { get; set; }
    public string? AnimeId { get; set; }
    public string? AnimeTitle { get; set; }
    public int EpisodeNumber { get; set; }
    public int? Season { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? LinkPath { get; set; }
    public long CreatedAt { get; set; }
    public long? ReadAt { get; set; }

    public bool IsRead => ReadAt.HasValue;
}

public sealed class NotificationsResponse { public List<NotificationDto> Items { get; set; } = new(); }
public sealed class NotificationCount { public int Count { get; set; } public long? NextAiringAt { get; set; } }

// ── Calendar / upcoming ──────────────────────────────────────────────────────

public sealed class UpcomingEpisodeDto
{
    public string AnimeId { get; set; } = "";
    public string Title { get; set; } = "";
    public int Episode { get; set; }
    public long AiringAt { get; set; }
    public string? CoverImage { get; set; }
}

public sealed class UpcomingResponse { public List<UpcomingEpisodeDto> Items { get; set; } = new(); }

// ── Stremio stream sources (/{config}/stream/{type}/{id}.json) ───────────────

/// <summary>One source returned by the Stremio stream endpoint. A direct
/// <see cref="Url"/> is playable (LibVLC on MAUI / HTML5 on web); an
/// <see cref="ExternalUrl"/> (e.g. "Watch on Crunchyroll", "Manage Entry") opens
/// out of the player.</summary>
public sealed class StremioStream
{
    public string? Url { get; set; }
    public string? ExternalUrl { get; set; }
    public string? Name { get; set; }
    public string? Title { get; set; }

    public bool IsPlayable => !string.IsNullOrEmpty(Url);
    public string Label => Title ?? Name ?? "Source";
}

public sealed class StremioStreamsResponse { public List<StremioStream> Streams { get; set; } = new(); }
