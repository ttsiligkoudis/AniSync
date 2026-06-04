using AniSync.Client.Models;

namespace AniSync.Client.Services;

/// <summary>
/// Thin client over the AniSync backend's JSON API. The concrete
/// <see cref="AniSyncApi"/> wraps an HttpClient whose BaseAddress is set
/// per-head from <see cref="IAppEnvironment.ApiBaseUrl"/>. All endpoints used
/// here already exist on the server (/api/v1, /api/v1/me, /Home/*).
/// </summary>
public interface IAniSyncApi
{
    // Search + discovery
    Task<IReadOnlyList<SuggestMatch>> SuggestAsync(string title, int limit = 8, CancellationToken ct = default);
    /// <summary>Relevance-ranked best-match resolver — /api/v1/match (header search "Enter to open top result").</summary>
    Task<IReadOnlyList<MatchResult>> MatchAsync(string title, int limit = 8, CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> DiscoverAsync(string kind, string? genre = null, string? skip = null, CancellationToken ct = default);
    /// <summary>Video (movie/series) discovery via Trakt — /api/v1/discover/video/{type}/{mode}.</summary>
    Task<IReadOnlyList<MetaDto>> DiscoverVideoAsync(string type, string mode, string? skip = null, CancellationToken ct = default);
    /// <summary>Anime tagged with a given AniList tag — /api/v1/discover/by-tag/{tag}.</summary>
    Task<TagMediaResponse> DiscoverByTagAsync(string tag, string? skip = null, CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> AiringTodayAsync(CancellationToken ct = default);

    // Discover browse-by (tags / studios / staff directories + detail)
    Task<IReadOnlyList<TagSummaryDto>> TagsAsync(CancellationToken ct = default);
    Task<StudiosListResponse> StudiosAsync(int page = 1, CancellationToken ct = default);
    Task<StudioMediaResponse> StudioMediaAsync(int studioId, string? skip = null, CancellationToken ct = default);
    Task<StaffMediaResponse> StaffMediaAsync(int staffId, string? skip = null, CancellationToken ct = default);
    Task<ActorsListResponse> ActorsAsync(int page = 1, string? search = null, CancellationToken ct = default);
    Task<ActorCreditsResponse?> ActorCreditsAsync(int tmdbId, CancellationToken ct = default);
    Task<SeasonStatsResponse?> SeasonStatsAsync(CancellationToken ct = default);

    // Dashboard + library (user-scoped — require X-AniSync-Config)
    Task<AnilistUserStatsDto?> AnilistStatsAsync(CancellationToken ct = default);
    Task<TraktUserStatsDto?> TraktStatsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> ContinueWatchingAsync(int limit = 15, CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> ListAsync(string status, CancellationToken ct = default);
    Task<LibraryResponse?> LibraryAsync(string? status = null, CancellationToken ct = default);

    // Detail + watch
    Task<MetaDto?> AnimeAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<StreamingLinkDto>> StreamsAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<EpisodeInfoDto>> EpisodesAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> RelatedAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> RecommendationsAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<LinkDto>> SimilarAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<LinkDto>> SupplementaryAsync(string id, CancellationToken ct = default);
    Task<string?> TrailerAsync(string id, CancellationToken ct = default);
    Task<AnimeSourceLinksDto?> SourceLinksAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<StremioStream>> PlaybackSourcesAsync(string config, string type, string streamId, CancellationToken ct = default);
    Task<IReadOnlyList<SubtitleTrack>> SubtitlesAsync(string id, int episode, int? season = null, CancellationToken ct = default);
    Task<IReadOnlyList<SkipMarker>> SkipMarkersAsync(string id, int episode, CancellationToken ct = default);
    Task<FillerResponse?> FillerAsync(string titleOrId, CancellationToken ct = default);

    // Auth (which sign-in providers the backend can start)
    Task<AuthProvidersDto> AuthProvidersAsync(CancellationToken ct = default);

    // Configure / account / advanced (header-authed; stored v5 configs only)
    Task<ConfigStateDto?> GetConfigAsync(CancellationToken ct = default);
    Task<SaveFlagsResult?> SaveFlagsAsync(byte flags1, byte flags2, byte flags3, CancellationToken ct = default);
    Task<SaveFlagsResult?> ResetConfigAsync(CancellationToken ct = default);
    Task<bool> DeleteConfigAsync(CancellationToken ct = default);
    Task<RegenerateResult?> RegenerateUidAsync(CancellationToken ct = default);
    Task<bool> SignOutEverywhereAsync(CancellationToken ct = default);
    Task<bool> UnlinkAsync(string service, CancellationToken ct = default);
    Task<ScrobbleTokenDto?> GetScrobbleAsync(CancellationToken ct = default);
    Task<ScrobbleTokenDto?> RotateScrobbleAsync(CancellationToken ct = default);
    Task<bool> SetPlexUsernameAsync(string? username, CancellationToken ct = default);
    Task<AddAddonResult?> AddStreamAddonAsync(string manifestUrl, CancellationToken ct = default);
    Task<bool> RemoveStreamAddonAsync(string manifestUrl, CancellationToken ct = default);
    Task<bool> ReorderStreamAddonsAsync(IReadOnlyList<string> urls, CancellationToken ct = default);
    Task<DebridResult?> AddDebridAddonsAsync(DebridSetupRequest request, CancellationToken ct = default);
    Task<StreamCatalogDto> StreamCatalogAsync(CancellationToken ct = default);
    /// <summary>Backfill: push every primary-library entry to the linked accounts. Streams
    /// NDJSON progress server-side; the client just awaits completion (success = 2xx).</summary>
    Task<bool> SyncAsync(CancellationToken ct = default);
    Task<string?> ExportConfigJsonAsync(CancellationToken ct = default);
    Task<RegenerateResult?> ImportConfigJsonAsync(string backupJson, CancellationToken ct = default);

    // Tracking (manage-entry modal + sync)
    Task<AnimeEntryDto?> GetEntryAsync(string id, int? season = null, CancellationToken ct = default);
    Task<SaveEntryResponse?> SaveEntryAsync(string id, EntrySaveRequest request, int? season = null, CancellationToken ct = default);
    Task<SaveEntryResponse?> DeleteEntryAsync(string id, int? season = null, CancellationToken ct = default);
    Task<LinkedResponse?> LinkedAsync(CancellationToken ct = default);
    Task<PromoteResponse?> PromotePrimaryAsync(string service, bool force = false, CancellationToken ct = default);
    Task<DiffResponse?> SyncDiffAsync(CancellationToken ct = default);
    Task<PreferencesDto?> GetPreferencesAsync(CancellationToken ct = default);
    Task<bool> SavePreferencesAsync(PreferencesDto prefs, CancellationToken ct = default);

    // Notifications + calendar
    Task<IReadOnlyList<NotificationDto>> NotificationsAsync(int limit = 20, int skip = 0, CancellationToken ct = default);
    Task<NotificationCount> NotificationCountAsync(CancellationToken ct = default);
    Task MarkNotificationReadAsync(long id, CancellationToken ct = default);
    Task MarkAllNotificationsReadAsync(CancellationToken ct = default);
    Task MarkNotificationsBulkReadAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
    Task DeleteNotificationAsync(long id, CancellationToken ct = default);
    Task DeleteNotificationsBulkAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
    Task<IReadOnlyList<UpcomingEpisodeDto>> UpcomingAsync(CancellationToken ct = default);
    /// <summary>Weekly calendar (recent + upcoming) for the selected day (yyyy-MM-dd; null = this week).</summary>
    Task<CalendarResponse?> CalendarAsync(string? day = null, CancellationToken ct = default);

    // Web push (bell + notifications page opt-in)
    Task<VapidKeyResponse?> PushVapidKeyAsync(CancellationToken ct = default);
    Task<PushStatusResponse?> PushStatusAsync(CancellationToken ct = default);
    Task<bool> PushSubscribeAsync(PushSubscribeRequestDto request, CancellationToken ct = default);
    Task<bool> PushUnsubscribeAsync(string endpoint, CancellationToken ct = default);
}
