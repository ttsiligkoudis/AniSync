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
    Task<IReadOnlyList<MetaDto>> DiscoverAsync(string kind, string? genre = null, string? skip = null, string? season = null, string? search = null, CancellationToken ct = default);
    /// <summary>Video (movie/series) discovery via Trakt — /api/v1/discover/video/{type}/{mode}.
    /// modes: trending|popular|anticipated|watched|recommended ("For You", needs Trakt connected).</summary>
    Task<IReadOnlyList<MetaDto>> DiscoverVideoAsync(string type, string mode, string? skip = null, string? genre = null, string? search = null, CancellationToken ct = default);
    /// <summary>Anime tagged with a given AniList tag — /api/v1/discover/by-tag/{tag}. 1-indexed page (server is page-based).</summary>
    Task<TagMediaResponse> DiscoverByTagAsync(string tag, int page = 1, CancellationToken ct = default);
    /// <summary>Legacy skip-offset overload for the Discover page's count-based tag pager. Prefer the page-based overload.</summary>
    Task<TagMediaResponse> DiscoverByTagAsync(string tag, string? skip, CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> AiringTodayAsync(CancellationToken ct = default);

    // Discover browse-by (tags / studios / staff directories + detail)
    Task<IReadOnlyList<TagSummaryDto>> TagsAsync(CancellationToken ct = default);
    Task<StudiosListResponse> StudiosAsync(int page = 1, string? search = null, CancellationToken ct = default);
    Task<StudioMediaResponse> StudioMediaAsync(int studioId, int page = 1, CancellationToken ct = default);
    Task<StaffMediaResponse> StaffMediaAsync(int staffId, string? skip = null, CancellationToken ct = default);
    Task<ActorsListResponse> ActorsAsync(int page = 1, string? search = null, CancellationToken ct = default);
    Task<ActorCreditsResponse?> ActorCreditsAsync(int tmdbId, CancellationToken ct = default);
    /// <summary>The movies/series Discover mode pills (server-authoritative, Trakt-gated) — /api/v1/discover/video-modes.</summary>
    Task<IReadOnlyList<VideoModeDto>> VideoModesAsync(CancellationToken ct = default);
    Task<SeasonStatsResponse?> SeasonStatsAsync(CancellationToken ct = default);

    // Dashboard + library (user-scoped — require X-AniSync-Config)
    Task<AnilistUserStatsDto?> AnilistStatsAsync(CancellationToken ct = default);
    Task<TraktUserStatsDto?> TraktStatsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> ContinueWatchingAsync(int limit = 15, CancellationToken ct = default);
    /// <summary>The user's merged tracker list for a status — /api/v1/me/list. <paramref name="genre"/>
    /// filters server-side; <paramref name="search"/> applies the server's ScoreMatch relevance re-rank.</summary>
    Task<IReadOnlyList<MetaDto>> ListAsync(string status, string? genre = null, string? search = null, CancellationToken ct = default);
    /// <summary>The user's movies / series library from Trakt — /api/v1/me/video-list.
    /// type = movie|series; list = current|completed|planning|paused|dropped. <paramref name="search"/>
    /// applies the server's ScoreMatch relevance re-rank.</summary>
    Task<IReadOnlyList<MetaDto>> VideoListAsync(string type, string list, string? search = null, CancellationToken ct = default);
    /// <summary>The user's Hidden section (titles hidden from Discover) — /api/v1/me/hidden?skip=. Paged 24/call.</summary>
    Task<IReadOnlyList<MetaDto>> HiddenAsync(string? skip = null, CancellationToken ct = default);
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

    // ── Watch: episode streams / subtitles / mark-watched / scrobble (header-authed) ──
    /// <summary>Bootstrap the watch page's source picker — GET /api/v1/me/episode-streams
    /// (no addonIndex): configured stream addons (for per-addon fan-out), external links,
    /// and AniSkip markers. <paramref name="type"/> = "movie"/"series" for Cinemeta video.</summary>
    Task<EpisodeStreamsBootstrap?> EpisodeStreamsBootstrapAsync(string id, int? season, int episode, string? type = null, CancellationToken ct = default);
    /// <summary>Fan out one configured stream addon's debrid rows — GET
    /// /api/v1/me/episode-streams?addonIndex=N. Returns the enriched rows (quality / size /
    /// seeders / language / provider / infoHash / isHevc / source / hdr / audio /
    /// audioUnsupported / description) for the client to merge + cap + warn on.</summary>
    Task<IReadOnlyList<EpisodeStreamDto>> EpisodeStreamsAsync(string id, int addonIndex, int? season, int episode, string? type = null, CancellationToken ct = default);
    /// <summary>Release-matched subtitle lookup for the picked source — GET
    /// /api/v1/me/episode-subtitles. <paramref name="filename"/> is the chosen source's
    /// file name (the OpenSubtitles timing-match signal).</summary>
    Task<EpisodeSubtitlesResponse?> EpisodeSubtitlesAsync(string id, int? season, int episode, string? filename = null, string? type = null, CancellationToken ct = default);
    /// <summary>Mark an episode watched (70 % / external hand-off) — POST /api/v1/me/mark-watched.
    /// Anime → primary tracker + fan-out; movie / series → Trakt history. <paramref name="sourceUrl"/>,
    /// when set, triggers the debrid-placeholder probe before persisting.</summary>
    Task<MarkWatchedResult?> MarkWatchedAsync(string id, int episode, int? season = null, string? type = null, string? sourceUrl = null, CancellationToken ct = default);
    /// <summary>Park a movie / series playback position in Trakt's Continue Watching — POST
    /// /api/v1/me/scrobble-progress. <paramref name="progress"/> is 0-100 (the &lt;1 % and 95 %+
    /// tail are filtered client-side).</summary>
    Task<ScrobbleProgressResult?> ScrobbleProgressAsync(string id, string type, double progress, int? season = null, int episode = 0, CancellationToken ct = default);
    /// <summary>Follow a resolver URL's 302 to the debrid CDN URL — GET /api/v1/resolve-stream.
    /// Host-allow-listed server-side; returns the resolved URL or null on failure.</summary>
    Task<string?> ResolveStreamAsync(string url, CancellationToken ct = default);
    /// <summary>Builds the same-origin subtitle-proxy URL (GET /api/v1/subtitle?url=…) for a
    /// &lt;track src&gt; — the server fetches the upstream URL and converts SRT→VTT with CORS
    /// so the player's track load needs no cross-origin opt-in. Synchronous (no network).</summary>
    string SubtitleProxyUrl(string upstreamUrl);

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
    /// <summary>One library entry by media id (Manage Entry modal). For a cross-service
    /// franchise the response also carries the per-cour Season dropdown options, the
    /// resolved cour id, and the primary service (for the per-service score range). Refetch
    /// a specific cour by calling again with that cour's id.</summary>
    Task<EntryResponse?> GetEntryAsync(string id, int? season = null, CancellationToken ct = default);
    Task<SaveEntryResponse?> SaveEntryAsync(string id, EntrySaveRequest request, int? season = null, CancellationToken ct = default);
    Task<SaveEntryResponse?> DeleteEntryAsync(string id, int? season = null, CancellationToken ct = default);
    /// <summary>Per-user detail-page state (list entry + hidden flag) in one call —
    /// /api/v1/me/state/{id}. Drives the hero pill, quick-add heart, and Hide button.</summary>
    Task<DetailStateDto?> DetailStateAsync(string id, int? season = null, CancellationToken ct = default);
    /// <summary>Quick-add heart toggle for the Currently Watching list — POST /api/v1/me/watching/toggle.</summary>
    Task<ToggleWatchingResult?> ToggleWatchingAsync(string id, int? season = null, CancellationToken ct = default);
    /// <summary>Hide / unhide an anime from Discover — POST /api/v1/me/hidden/toggle.</summary>
    Task<ToggleHiddenResult?> ToggleHiddenAsync(string id, string? title = null, string? imageUrl = null, string? mediaType = null, CancellationToken ct = default);
    Task<LinkedResponse?> LinkedAsync(CancellationToken ct = default);
    Task<PromoteResponse?> PromotePrimaryAsync(string service, bool force = false, CancellationToken ct = default);
    Task<DiffResponse?> SyncDiffAsync(CancellationToken ct = default);
    Task<PreferencesDto?> GetPreferencesAsync(CancellationToken ct = default);
    Task<bool> SavePreferencesAsync(PreferencesDto prefs, CancellationToken ct = default);
    /// <summary>The user's saved dashboard layout JSON ([{key,visible}]) or null — /api/v1/me/dashboard-layout.</summary>
    Task<string?> GetDashboardLayoutAsync(CancellationToken ct = default);
    Task<bool> SaveDashboardLayoutAsync(string layoutJson, CancellationToken ct = default);

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
