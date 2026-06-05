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
    /// <summary>Started watching date (server serialises DateTime? as ISO-8601).</summary>
    public DateTime? StartedAt { get; set; }
    /// <summary>Finished watching date.</summary>
    public DateTime? FinishedAt { get; set; }
}

/// <summary>One option in the Manage Entry "Season" dropdown — a single mapped cour
/// of a cross-service franchise. <see cref="Id"/> is the service-prefixed per-cour id
/// (anilist:N / kitsu:N / mal:N) the modal refetches and saves against.</summary>
public sealed class EntrySeasonDto
{
    public string? Id { get; set; }
    public string? Label { get; set; }
    public int? TotalEpisodes { get; set; }
}

/// <summary>GET /api/v1/me/entries/{id}. <see cref="Entry"/> is the resolved cour's list
/// entry (null when that cour isn't on the user's list). <see cref="Seasons"/> carries the
/// per-cour dropdown options for a cross-service franchise (empty/null otherwise),
/// <see cref="SelectedEntryId"/> is the cour the entry was read against, and
/// <see cref="Service"/> is the primary provider's AnimeService int (0 Kitsu, 1 AniList,
/// 2 MyAnimeList, 3 Trakt) so the modal picks the right score range.</summary>
public sealed class EntryResponse
{
    public AnimeEntryDto? Entry { get; set; }
    public List<EntrySeasonDto>? Seasons { get; set; }
    public int? Service { get; set; }
    public string? SelectedEntryId { get; set; }
}

/// <summary>POST body for /api/v1/me/entries/{id}. Status is the canonical
/// ListStatus name (Watching/Completed/Planning/Paused/Dropped/Rewatching);
/// the server (JsonStringEnumConverter is registered) parses the string. Send a
/// null Status to remove the entry from the list (mirrors the DELETE path).</summary>
public sealed class EntrySaveRequest
{
    public string? Status { get; set; }
    public int? Progress { get; set; }
    public double? Score { get; set; }
    public string? Notes { get; set; }
    public int? RewatchCount { get; set; }
    /// <summary>Started watching date (yyyy-MM-dd).</summary>
    public string? StartedAt { get; set; }
    /// <summary>Finished watching date (yyyy-MM-dd).</summary>
    public string? FinishedAt { get; set; }
}

public sealed class SaveEntryResponse { public bool Ok { get; set; } public string? Primary { get; set; } public bool? Removed { get; set; } }

// ── Detail-page per-user state + interactive toggles ─────────────────────────

/// <summary>Per-user detail-page state — GET /api/v1/me/state/{id}. Drives the
/// hero's user-state pill, the quick-add heart, and the Hide / Unhide button in a
/// single round-trip. Status is the raw provider value (the page normalises it);
/// fields are null/0/false when the anime isn't on the user's list or isn't hidden.</summary>
public sealed class DetailStateDto
{
    public bool OnList { get; set; }
    public string? Status { get; set; }
    public int Progress { get; set; }
    public int? TotalEpisodes { get; set; }
    public double? Score { get; set; }
    public bool IsHidden { get; set; }
}

/// <summary>Result of the quick-add heart toggle (POST /api/v1/me/watching/toggle).
/// <c>Watching</c> is the new heart state; <c>Hidden</c> is true when the entry is in
/// another list and the heart should be dropped (managed via the modal instead).</summary>
public sealed class ToggleWatchingResult { public bool Ok { get; set; } public bool Watching { get; set; } public bool Hidden { get; set; } }

/// <summary>Result of the Hide / Unhide toggle (POST /api/v1/me/hidden/toggle).
/// <c>Hidden</c> is the new state.</summary>
public sealed class ToggleHiddenResult { public bool Ok { get; set; } public bool Hidden { get; set; } }

// ── Login providers (which sign-in options the backend can start) ─────────────

/// <summary>GET /api/v1/auth/providers — Kitsu is always available (username/
/// password grant); the OAuth providers are true only when their ClientId is
/// configured on the host. The login picker hides unconfigured providers.</summary>
public sealed class AuthProvidersDto
{
    public bool Kitsu { get; set; } = true;
    public bool Anilist { get; set; }
    public bool Mal { get; set; }
    public bool Trakt { get; set; }
}

// ── Linked accounts / primary swap / sync (settings + sync surfaces) ──────────

public sealed class LinkedSummaryDto { public string? Service { get; set; } public bool NeedsReauth { get; set; } }
public sealed class LinkedResponse { public string? Primary { get; set; } public List<LinkedSummaryDto> Linked { get; set; } = new(); }

/// <summary>Result of POST /api/v1/me/primary/{service}. Reason carries the
/// failure cause when Ok is false (collision / needs-reauth / not-linked / …).</summary>
public sealed class PromoteResponse { public bool Ok { get; set; } public string? Primary { get; set; } public string? Reason { get; set; } }

public sealed class LibraryResponse { public string? Primary { get; set; } public string? Status { get; set; } public List<AnimeEntryDto> Entries { get; set; } = new(); }

/// <summary>Named user preferences (GET/POST /api/v1/me/preferences). Stored is
/// false for inline configs with no persisted row (read-only).</summary>
public sealed class PreferencesDto
{
    public bool GroupSeasons { get; set; }
    public bool HideUnaired { get; set; }
    public bool ShowAdult { get; set; }
    public bool DisableAutoTrack { get; set; }
    public bool Stored { get; set; }
}

// ── Sync diff (primary vs each linked secondary) ─────────────────────────────

public sealed class DiffEntrySnapshot { public string? MediaId { get; set; } public string? Status { get; set; } public int Progress { get; set; } }
public sealed class DiffMissing { public DiffEntrySnapshot? Primary { get; set; } }
public sealed class DiffMismatched { public DiffEntrySnapshot? Primary { get; set; } public DiffEntrySnapshot? Secondary { get; set; } }
public sealed class DiffPerService
{
    public string? Service { get; set; }
    public bool? NeedsReauth { get; set; }
    public string? Error { get; set; }
    public int? MissingCount { get; set; }
    public int? MismatchedCount { get; set; }
    public List<DiffMissing>? Missing { get; set; }
    public List<DiffMismatched>? Mismatched { get; set; }
}
public sealed class DiffResponse { public string? Primary { get; set; } public int PrimaryCount { get; set; } public List<DiffPerService> Diffs { get; set; } = new(); }

// ── Web push (bell + notifications page opt-in) ──────────────────────────────

public sealed class VapidKeyResponse { public bool Enabled { get; set; } public string? PublicKey { get; set; } }
public sealed class PushStatusResponse { public bool Enabled { get; set; } public bool Subscribed { get; set; } }
public sealed class PushSubscribeKeysDto { public string? P256dh { get; set; } public string? Auth { get; set; } }
public sealed class PushSubscribeRequestDto { public string? Endpoint { get; set; } public PushSubscribeKeysDto? Keys { get; set; } }
public sealed class PushUnsubscribeRequestDto { public string? Endpoint { get; set; } }

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

// ── Weekly calendar (/api/v1/me/calendar) — mirrors CalendarViewModel ─────────

public sealed class CalendarEpisodeDto
{
    public string? Kind { get; set; }          // "anime" | "series"
    public string? Title { get; set; }
    public int? Season { get; set; }
    public int Episode { get; set; }
    public long AiringAt { get; set; }          // Unix seconds, UTC
    public string? CoverImage { get; set; }
    public string? LinkPath { get; set; }       // deep link to the episode's watch page
    public string? EpisodeTitle { get; set; }
}

public sealed class CalendarDayDto
{
    public string? DateIso { get; set; }
    public string? Label { get; set; }
    public bool IsToday { get; set; }
    public bool IsSelected { get; set; }
    public bool HasAnime { get; set; }
    public bool HasSeries { get; set; }
    public List<CalendarEpisodeDto> Episodes { get; set; } = new();
}

public sealed class CalendarResponse
{
    public List<CalendarDayDto> Days { get; set; } = new();
    public string? SelectedDateIso { get; set; }
    public string? PrevDate { get; set; }
    public string? NextDate { get; set; }
    public bool SelectedIsToday { get; set; }
    public int TotalEpisodes { get; set; }
}

// ── Subtitles (/api/v1/anime/{id}/episodes/{ep}/subtitles) ───────────────────

public sealed class SubtitleTrackDto
{
    public string? Lang { get; set; }
    public string? Label { get; set; }
    public string? Url { get; set; }
    public string? Source { get; set; }
}

public sealed class SubtitlesApiResponse { public List<SubtitleTrackDto> Subtitles { get; set; } = new(); }

// StremioStream / StremioStreamsResponse are defined in ApiModels.cs (the
// "Playback sources" section) — kept there to avoid a duplicate definition.
