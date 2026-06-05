using System.Net.Http.Json;
using AniSync.Client.Models;
using Microsoft.Extensions.Logging;

namespace AniSync.Client.Services;

/// <summary>
/// Default <see cref="IAniSyncApi"/> implementation. Registered as a typed
/// HttpClient per head; the head sets BaseAddress to the backend URL. User-scoped
/// endpoints (/api/v1/me/*) authenticate with the <c>X-AniSync-Config</c> header
/// carrying the user's config string — that string is the credential AND the
/// Stremio addon config, held in <see cref="AppState.StreamConfig"/>. Every call
/// degrades to an empty/null result on failure so one dead shelf never breaks
/// the page.
/// </summary>
public sealed class AniSyncApi : IAniSyncApi
{
    private const string ConfigHeader = "X-AniSync-Config";

    // Marks first-party app traffic (the web head AND the MAUI native apps — Windows /
    // Android / iOS) so the API's rate limiter can exempt our own apps. The 60/min cap
    // is meant for third-party API consumers, not the UI's own dashboard fan-out.
    public const string ClientHeaderName = "X-AniSync-Client";
    public const string ClientHeaderValue = "anisync-app";

    private readonly HttpClient _http;
    private readonly AppState _state;
    private readonly IAppEnvironment _env;
    private readonly ILogger<AniSyncApi> _logger;

    public AniSyncApi(HttpClient http, AppState state, IAppEnvironment env, ILogger<AniSyncApi> logger)
    {
        _http = http;
        _state = state;
        _env = env;
        _logger = logger;
        // Tag every request as first-party app traffic so the API rate limiter exempts it
        // (covers all heads — web + MAUI — since they share this typed client).
        if (!_http.DefaultRequestHeaders.Contains(ClientHeaderName))
            _http.DefaultRequestHeaders.Add(ClientHeaderName, ClientHeaderValue);
    }

    // Resolve the backend base address on first use rather than in the constructor.
    // On the Web head IAppEnvironment.ApiBaseUrl reads NavigationManager.BaseUri,
    // which only works inside a live circuit/request — touching it in the ctor would
    // throw during the container's build-time validation (the ctor runs then too).
    // By the first API call we're always in an initialised context.
    private void EnsureBaseAddress()
    {
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_env.ApiBaseUrl))
            _http.BaseAddress = new Uri(_env.ApiBaseUrl);
    }

    public async Task<IReadOnlyList<SuggestMatch>> SuggestAsync(string title, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<SuggestMatch>();
        var resp = await GetOrDefault<SuggestResponse>(
            $"api/v1/suggest?title={Uri.EscapeDataString(title)}&limit={limit}", ct);
        return resp?.Matches ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> DiscoverAsync(string kind, string? genre = null, string? skip = null, string? season = null, string? search = null, CancellationToken ct = default)
    {
        var url = $"api/v1/discover/{Uri.EscapeDataString(kind)}";
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(genre)) q.Add($"genre={Uri.EscapeDataString(genre)}");
        if (!string.IsNullOrWhiteSpace(season)) q.Add($"season={Uri.EscapeDataString(season)}");
        if (!string.IsNullOrWhiteSpace(search)) q.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(skip)) q.Add($"skip={Uri.EscapeDataString(skip)}");
        if (q.Count > 0) url += "?" + string.Join("&", q);
        var resp = await GetOrDefault<MetaListResponse>(url, ct);
        return resp?.Results ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> DiscoverVideoAsync(string type, string mode, string? skip = null, string? genre = null, string? search = null, CancellationToken ct = default)
    {
        var url = $"api/v1/discover/video/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(mode)}";
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(skip)) q.Add($"skip={Uri.EscapeDataString(skip)}");
        if (!string.IsNullOrWhiteSpace(genre)) q.Add($"genre={Uri.EscapeDataString(genre)}");
        if (!string.IsNullOrWhiteSpace(search)) q.Add($"search={Uri.EscapeDataString(search)}");
        if (q.Count > 0) url += "?" + string.Join("&", q);
        var resp = await GetOrDefault<MetaListResponse>(url, ct);
        return resp?.Results ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> AiringTodayAsync(CancellationToken ct = default)
    {
        var resp = await GetOrDefault<AiringTodayResponse>("api/v1/airing/today", ct);
        return resp?.Items ?? new();
    }

    public async Task<AnilistUserStatsDto?> AnilistStatsAsync(CancellationToken ct = default)
    {
        var resp = await GetOrDefault<UserStatsResponse>("api/v1/me/stats", ct);
        return resp?.Stats;
    }

    public async Task<TraktUserStatsDto?> TraktStatsAsync(CancellationToken ct = default)
    {
        var resp = await GetOrDefault<TraktStatsEnvelope>("Home/TraktStatsData", ct);
        return resp is { Success: true } ? resp.Stats : null;
    }

    public async Task<IReadOnlyList<MetaDto>> ContinueWatchingAsync(int limit = 15, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<ContinueWatchingResponse>($"api/v1/me/continue-watching?limit={limit}", ct);
        return resp?.Items ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> ListAsync(string status, string? genre = null, string? search = null, CancellationToken ct = default)
    {
        var url = $"api/v1/me/list?status={Uri.EscapeDataString(status)}";
        // Genre + search are filtered server-side (MergedListService genre filter + ScoreMatch
        // relevance re-rank) so the library matches the MVC LibraryController.Page behaviour.
        if (!string.IsNullOrWhiteSpace(genre)) url += $"&genre={Uri.EscapeDataString(genre)}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        var resp = await GetOrDefault<MetaListResponse>(url, ct);
        return resp?.Results ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> VideoListAsync(string type, string list, string? search = null, CancellationToken ct = default)
    {
        var url = $"api/v1/me/video-list?type={Uri.EscapeDataString(type)}&list={Uri.EscapeDataString(list)}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        var resp = await GetOrDefault<MetaListResponse>(url, ct);
        return resp?.Results ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> HiddenAsync(string? skip = null, CancellationToken ct = default)
    {
        var url = "api/v1/me/hidden";
        if (!string.IsNullOrWhiteSpace(skip)) url += $"?skip={Uri.EscapeDataString(skip)}";
        var resp = await GetOrDefault<MetaListResponse>(url, ct);
        return resp?.Results ?? new();
    }

    public async Task<MetaDto?> AnimeAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<AnimeResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}", ct);
        return resp?.Meta;
    }

    public Task<VideoMetaResponse?> VideoAsync(string type, string id, CancellationToken ct = default)
        => GetOrDefault<VideoMetaResponse>(
            $"api/v1/video/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(id)}", ct);

    public async Task<IReadOnlyList<StreamingLinkDto>> StreamsAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<StreamsResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}/streams", ct);
        return resp?.Streams ?? new();
    }

    public async Task<IReadOnlyList<EpisodeInfoDto>> EpisodesAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<EpisodesResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}/episodes", ct);
        return resp?.Episodes ?? new();
    }

    public async Task<IReadOnlyList<SubtitleTrack>> SubtitlesAsync(string id, int episode, int? season = null, CancellationToken ct = default)
    {
        var url = $"api/v1/anime/{Uri.EscapeDataString(id)}/episodes/{episode}/subtitles";
        if (season.HasValue) url += $"?season={season.Value}";
        var resp = await GetOrDefault<SubtitlesApiResponse>(url, ct);
        return (resp?.Subtitles ?? new())
            .Where(s => !string.IsNullOrEmpty(s.Url))
            .Select(s => new SubtitleTrack(s.Url!, s.Label ?? s.Lang ?? "Subtitle", s.Lang))
            .ToList();
    }

    public async Task<IReadOnlyList<StremioStream>> PlaybackSourcesAsync(string config, string type, string streamId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config)) return Array.Empty<StremioStream>();
        var url = $"{config.Trim('/')}/stream/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(streamId)}.json";
        var resp = await GetOrDefault<StremioStreamsResponse>(url, ct);
        return resp?.Streams.Where(s => !string.IsNullOrEmpty(s.Url)).ToList() ?? new();
    }

    // ── Watch: episode streams / subtitles / mark-watched / scrobble ──────────

    public Task<EpisodeStreamsBootstrap?> EpisodeStreamsBootstrapAsync(string id, int? season, int episode, string? type = null, CancellationToken ct = default)
        => GetOrDefault<EpisodeStreamsBootstrap>(EpisodeStreamsUrl(id, season, episode, type, addonIndex: null), ct);

    public async Task<IReadOnlyList<EpisodeStreamDto>> EpisodeStreamsAsync(string id, int addonIndex, int? season, int episode, string? type = null, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<EpisodeStreamsResponse>(EpisodeStreamsUrl(id, season, episode, type, addonIndex), ct);
        return resp?.DebridStreams ?? new();
    }

    // Shared URL builder for the two episode-streams modes — bootstrap (addonIndex
    // null) and per-addon fan-out. Movie-typed entries forward ?type=movie so the
    // server drops season+episode from the addon id shape.
    private static string EpisodeStreamsUrl(string id, int? season, int episode, string? type, int? addonIndex)
    {
        var url = $"api/v1/me/episode-streams?id={Uri.EscapeDataString(id)}&episode={episode}";
        if (season.HasValue) url += $"&season={season.Value}";
        if (!string.IsNullOrWhiteSpace(type)) url += $"&type={Uri.EscapeDataString(type)}";
        if (addonIndex.HasValue) url += $"&addonIndex={addonIndex.Value}";
        return url;
    }

    public async Task<EpisodeSubtitlesResponse?> EpisodeSubtitlesAsync(string id, int? season, int episode, string? filename = null, string? type = null, CancellationToken ct = default)
    {
        var url = $"api/v1/me/episode-subtitles?id={Uri.EscapeDataString(id)}&episode={episode}";
        if (season.HasValue) url += $"&season={season.Value}";
        if (!string.IsNullOrWhiteSpace(filename)) url += $"&filename={Uri.EscapeDataString(filename)}";
        if (!string.IsNullOrWhiteSpace(type)) url += $"&type={Uri.EscapeDataString(type)}";
        return await GetOrDefault<EpisodeSubtitlesResponse>(url, ct);
    }

    public Task<MarkWatchedResult?> MarkWatchedAsync(string id, int episode, int? season = null, string? type = null, string? sourceUrl = null, CancellationToken ct = default)
        => PostJson<object, MarkWatchedResult>("api/v1/me/mark-watched",
            new { id, season, episode, type, sourceUrl }, ct);

    public Task<ScrobbleProgressResult?> ScrobbleProgressAsync(string id, string type, double progress, int? season = null, int episode = 0, CancellationToken ct = default)
        => PostJson<object, ScrobbleProgressResult>("api/v1/me/scrobble-progress",
            new { id, type, season, episode, progress }, ct);

    public async Task<string?> ResolveStreamAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var resp = await GetOrDefault<ResolveStreamResult>(
            $"api/v1/resolve-stream?url={Uri.EscapeDataString(url)}", ct);
        return resp?.ResolvedUrl;
    }

    public string SubtitleProxyUrl(string upstreamUrl)
    {
        if (string.IsNullOrWhiteSpace(upstreamUrl)) return upstreamUrl;
        // Absolute so a <track src> resolves to the backend origin regardless of the
        // head's own origin. The proxy (GET /api/v1/subtitle) does the same-origin
        // SRT→VTT conversion + CORS headers so the player's track load doesn't need a
        // cross-origin opt-in. Falls back to a relative path when ApiBaseUrl isn't
        // resolvable yet (web head, build-time) — still correct same-origin on the web.
        var query = $"api/v1/subtitle?url={Uri.EscapeDataString(upstreamUrl)}";
        var baseUrl = _env.ApiBaseUrl;
        return string.IsNullOrWhiteSpace(baseUrl)
            ? "/" + query
            : $"{baseUrl.TrimEnd('/')}/{query}";
    }

    public async Task<AuthProvidersDto> AuthProvidersAsync(CancellationToken ct = default)
        => await GetOrDefault<AuthProvidersDto>("api/v1/auth/providers", ct) ?? new();

    // ── Configure / account / advanced ───────────────────────────────────────

    public Task<ConfigStateDto?> GetConfigAsync(CancellationToken ct = default)
        => GetOrDefault<ConfigStateDto>("api/v1/me/config", ct);

    public Task<SaveFlagsResult?> SaveFlagsAsync(byte flags1, byte flags2, byte flags3, CancellationToken ct = default)
        => PostJson<object, SaveFlagsResult>("api/v1/me/config/flags", new { flags1, flags2, flags3 }, ct);

    public Task<SaveFlagsResult?> ResetConfigAsync(CancellationToken ct = default)
        => PostJson<object, SaveFlagsResult>("api/v1/me/config/reset", new { }, ct);

    public async Task<bool> DeleteConfigAsync(CancellationToken ct = default)
        => (await SendJson<OkResult>(HttpMethod.Delete, "api/v1/me/config", null, ct))?.Ok ?? false;

    public Task<RegenerateResult?> RegenerateUidAsync(CancellationToken ct = default)
        => PostJson<object, RegenerateResult>("api/v1/me/config/regenerate", new { }, ct);

    public Task<bool> SignOutEverywhereAsync(CancellationToken ct = default)
        => PostForOk("api/v1/me/signout-everywhere", new { }, ct);

    public Task<bool> UnlinkAsync(string service, CancellationToken ct = default)
        => PostForOk($"api/v1/me/unlink/{Uri.EscapeDataString(service)}", new { }, ct);

    public Task<ScrobbleTokenDto?> GetScrobbleAsync(CancellationToken ct = default)
        => GetOrDefault<ScrobbleTokenDto>("api/v1/me/scrobble", ct);

    public Task<ScrobbleTokenDto?> RotateScrobbleAsync(CancellationToken ct = default)
        => PostJson<object, ScrobbleTokenDto>("api/v1/me/scrobble/rotate", new { }, ct);

    public Task<bool> SetPlexUsernameAsync(string? username, CancellationToken ct = default)
        => PostForOk("api/v1/me/plex-username", new { username }, ct);

    public Task<AddAddonResult?> AddStreamAddonAsync(string manifestUrl, CancellationToken ct = default)
        => PostJson<object, AddAddonResult>("api/v1/me/stream-addons", new { manifestUrl }, ct);

    public async Task<bool> RemoveStreamAddonAsync(string manifestUrl, CancellationToken ct = default)
        => (await SendJson<RemovedResult>(HttpMethod.Delete, "api/v1/me/stream-addons", new { manifestUrl }, ct))?.Removed ?? false;

    public async Task<bool> ReorderStreamAddonsAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
        => (await PostJson<object, ChangedResult>("api/v1/me/stream-addons/reorder", new { urls }, ct))?.Changed ?? false;

    public Task<DebridResult?> AddDebridAddonsAsync(DebridSetupRequest request, CancellationToken ct = default)
        => PostJson<DebridSetupRequest, DebridResult>("api/v1/me/stream-addons/debrid", request, ct);

    public async Task<StreamCatalogDto> StreamCatalogAsync(CancellationToken ct = default)
        => await GetOrDefault<StreamCatalogDto>("api/v1/stream-catalog", ct) ?? new();

    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureBaseAddress();
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/me/sync");
            if (!string.IsNullOrEmpty(_state.StreamConfig))
                req.Headers.TryAddWithoutValidation(ConfigHeader, _state.StreamConfig);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "POST api/v1/me/sync failed."); return false; }
    }

    public Task<string?> ExportConfigJsonAsync(CancellationToken ct = default)
        => GetRaw("api/v1/me/export", ct);

    public Task<RegenerateResult?> ImportConfigJsonAsync(string backupJson, CancellationToken ct = default)
        => PostRaw<RegenerateResult>("api/v1/me/import", backupJson, ct);

    // Small private result shapes for the boolean-ish endpoints.
    private sealed class RemovedResult { public bool Removed { get; set; } }
    private sealed class ChangedResult { public bool Changed { get; set; } }

    public async Task<EntryResponse?> GetEntryAsync(string id, int? season = null, string? type = null, CancellationToken ct = default)
    {
        var url = $"api/v1/me/entries/{Uri.EscapeDataString(id)}";
        var q = new List<string>();
        if (season.HasValue) q.Add($"season={season.Value}");
        if (!string.IsNullOrWhiteSpace(type)) q.Add($"type={Uri.EscapeDataString(type)}");
        if (q.Count > 0) url += "?" + string.Join("&", q);
        return await GetOrDefault<EntryResponse>(url, ct);
    }

    public async Task<SaveEntryResponse?> SaveEntryAsync(string id, EntrySaveRequest request, int? season = null, string? type = null, CancellationToken ct = default)
    {
        var url = $"api/v1/me/entries/{Uri.EscapeDataString(id)}";
        var q = new List<string>();
        if (season.HasValue) q.Add($"season={season.Value}");
        if (!string.IsNullOrWhiteSpace(type)) q.Add($"type={Uri.EscapeDataString(type)}");
        if (q.Count > 0) url += "?" + string.Join("&", q);
        return await PostJson<EntrySaveRequest, SaveEntryResponse>(url, request, ct);
    }

    public async Task<DetailStateDto?> DetailStateAsync(string id, int? season = null, string? type = null, CancellationToken ct = default)
    {
        var url = $"api/v1/me/state/{Uri.EscapeDataString(id)}";
        var q = new List<string>();
        if (season.HasValue) q.Add($"season={season.Value}");
        if (!string.IsNullOrWhiteSpace(type)) q.Add($"type={Uri.EscapeDataString(type)}");
        if (q.Count > 0) url += "?" + string.Join("&", q);
        return await GetOrDefault<DetailStateDto>(url, ct);
    }

    public Task<ToggleWatchingResult?> ToggleWatchingAsync(string id, int? season = null, string? type = null, CancellationToken ct = default)
        => PostJson<object, ToggleWatchingResult>("api/v1/me/watching/toggle", new { id, season, type }, ct);

    public Task<ToggleHiddenResult?> ToggleHiddenAsync(string id, string? title = null, string? imageUrl = null, string? mediaType = null, CancellationToken ct = default)
        => PostJson<object, ToggleHiddenResult>("api/v1/me/hidden/toggle",
            new { id, title, imageUrl, mediaType }, ct);

    public async Task<IReadOnlyList<NotificationDto>> NotificationsAsync(int limit = 20, int skip = 0, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<NotificationsResponse>($"api/v1/notifications?limit={limit}&skip={skip}", ct);
        return resp?.Items ?? new();
    }

    public async Task<NotificationCount> NotificationCountAsync(CancellationToken ct = default)
        => await GetOrDefault<NotificationCount>("api/v1/notifications/count", ct) ?? new();

    public async Task MarkNotificationReadAsync(long id, CancellationToken ct = default)
        => await PostJson<object, object>($"api/v1/notifications/{id}/read", new { }, ct);

    public async Task MarkAllNotificationsReadAsync(CancellationToken ct = default)
        => await PostJson<object, object>("api/v1/notifications/read-all", new { }, ct);

    public async Task<IReadOnlyList<UpcomingEpisodeDto>> UpcomingAsync(CancellationToken ct = default)
    {
        var resp = await GetOrDefault<UpcomingResponse>("api/v1/me/upcoming", ct);
        return resp?.Items ?? new();
    }

    public Task<CalendarResponse?> CalendarAsync(string? day = null, CancellationToken ct = default)
    {
        var url = "api/v1/me/calendar";
        if (!string.IsNullOrWhiteSpace(day)) url += $"?d={Uri.EscapeDataString(day)}";
        return GetOrDefault<CalendarResponse>(url, ct);
    }

    // ── Search (best-match resolver) ─────────────────────────────────────────

    public async Task<IReadOnlyList<MatchResult>> MatchAsync(string title, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<MatchResult>();
        var resp = await GetOrDefault<MatchResponse>(
            $"api/v1/match?title={Uri.EscapeDataString(title)}&limit={limit}", ct);
        return resp?.Matches ?? new();
    }

    // ── Discover browse-by ───────────────────────────────────────────────────

    public async Task<TagMediaResponse> DiscoverByTagAsync(string tag, int page = 1, CancellationToken ct = default)
    {
        // Server (ApiController.DiscoverByTag) is 1-indexed page-based; matches the StudiosAsync convention.
        var url = $"api/v1/discover/by-tag/{Uri.EscapeDataString(tag)}?page={page}";
        return await GetOrDefault<TagMediaResponse>(url, ct) ?? new();
    }

    // Legacy skip-offset overload kept ONLY so the Discover page's count-based tag pager
    // (which passes a string item-offset) keeps compiling and behaving exactly as before.
    // New callers (TagDetail) use the page-based overload above.
    public async Task<TagMediaResponse> DiscoverByTagAsync(string tag, string? skip, CancellationToken ct = default)
    {
        var url = $"api/v1/discover/by-tag/{Uri.EscapeDataString(tag)}";
        if (!string.IsNullOrWhiteSpace(skip)) url += $"?skip={Uri.EscapeDataString(skip)}";
        return await GetOrDefault<TagMediaResponse>(url, ct) ?? new();
    }

    public async Task<IReadOnlyList<TagSummaryDto>> TagsAsync(CancellationToken ct = default)
    {
        var resp = await GetOrDefault<TagsListResponse>("api/v1/tags", ct);
        return resp?.Tags ?? new();
    }

    public async Task<StudiosListResponse> StudiosAsync(int page = 1, string? search = null, CancellationToken ct = default)
    {
        var url = $"api/v1/studios?page={page}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        return await GetOrDefault<StudiosListResponse>(url, ct) ?? new();
    }

    public async Task<StudioMediaResponse> StudioMediaAsync(int studioId, int page = 1, CancellationToken ct = default)
    {
        var url = $"api/v1/studios/{studioId}/anime?page={page}";
        return await GetOrDefault<StudioMediaResponse>(url, ct) ?? new();
    }

    public async Task<StaffMediaResponse> StaffMediaAsync(int staffId, string? skip = null, CancellationToken ct = default)
    {
        var url = $"api/v1/staff/{staffId}/anime";
        if (!string.IsNullOrWhiteSpace(skip)) url += $"?skip={Uri.EscapeDataString(skip)}";
        return await GetOrDefault<StaffMediaResponse>(url, ct) ?? new();
    }

    public async Task<ActorsListResponse> ActorsAsync(int page = 1, string? search = null, CancellationToken ct = default)
    {
        var url = $"api/v1/actors?page={page}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        return await GetOrDefault<ActorsListResponse>(url, ct) ?? new();
    }

    public Task<ActorCreditsResponse?> ActorCreditsAsync(int tmdbId, CancellationToken ct = default)
        => GetOrDefault<ActorCreditsResponse>($"api/v1/actors/tmdb/{tmdbId}", ct);

    public async Task<IReadOnlyList<VideoModeDto>> VideoModesAsync(CancellationToken ct = default)
    {
        var resp = await GetOrDefault<VideoModesResponse>("api/v1/discover/video-modes", ct);
        return resp?.Modes ?? new();
    }

    public Task<SeasonStatsResponse?> SeasonStatsAsync(CancellationToken ct = default)
        => GetOrDefault<SeasonStatsResponse>("api/v1/stats/season", ct);

    // ── Library ──────────────────────────────────────────────────────────────

    public Task<LibraryResponse?> LibraryAsync(string? status = null, CancellationToken ct = default)
    {
        var url = "api/v1/me/library";
        if (!string.IsNullOrWhiteSpace(status)) url += $"?status={Uri.EscapeDataString(status)}";
        return GetOrDefault<LibraryResponse>(url, ct);
    }

    // ── Detail-page extras ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<MetaDto>> RelatedAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<MetaListResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}/related", ct);
        return resp?.Results ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> RecommendationsAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<MetaListResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}/recommendations", ct);
        return resp?.Results ?? new();
    }

    public async Task<IReadOnlyList<LinkDto>> SimilarAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<SimilarResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}/similar", ct);
        return resp?.Similar ?? new();
    }

    public async Task<IReadOnlyList<LinkDto>> SupplementaryAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<SupplementaryResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}/supplementary", ct);
        return resp?.Links ?? new();
    }

    public async Task<string?> TrailerAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<TrailerResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}/trailer", ct);
        return resp?.YoutubeId;
    }

    public async Task<AnimeSourceLinksDto?> SourceLinksAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<SourceLinksResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}/links", ct);
        return resp?.Links;
    }

    public async Task<IReadOnlyList<SkipMarker>> SkipMarkersAsync(string id, int episode, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<SkipResponse>($"api/v1/skip/{Uri.EscapeDataString(id)}/{episode}", ct);
        return resp?.Markers ?? new();
    }

    public Task<FillerResponse?> FillerAsync(string titleOrId, CancellationToken ct = default)
        => GetOrDefault<FillerResponse>($"api/v1/filler/{Uri.EscapeDataString(titleOrId)}", ct);

    // ── Tracking writes (delete / linked / primary / sync diff) ──────────────

    public async Task<SaveEntryResponse?> DeleteEntryAsync(string id, int? season = null, CancellationToken ct = default)
    {
        var url = $"api/v1/me/entries/{Uri.EscapeDataString(id)}";
        if (season.HasValue) url += $"?season={season.Value}";
        return await SendJson<SaveEntryResponse>(HttpMethod.Delete, url, null, ct);
    }

    public Task<LinkedResponse?> LinkedAsync(CancellationToken ct = default)
        => GetOrDefault<LinkedResponse>("api/v1/me/linked", ct);

    public Task<PromoteResponse?> PromotePrimaryAsync(string service, bool force = false, CancellationToken ct = default)
        => PostJson<object, PromoteResponse>(
            $"api/v1/me/primary/{Uri.EscapeDataString(service)}?force={(force ? "true" : "false")}", new { }, ct);

    public Task<DiffResponse?> SyncDiffAsync(CancellationToken ct = default)
        => GetOrDefault<DiffResponse>("api/v1/me/sync/diff", ct);

    public Task<PreferencesDto?> GetPreferencesAsync(CancellationToken ct = default)
        => GetOrDefault<PreferencesDto>("api/v1/me/preferences", ct);

    public Task<bool> SavePreferencesAsync(PreferencesDto prefs, CancellationToken ct = default)
        => PostForOk("api/v1/me/preferences", prefs, ct);

    public async Task<string?> GetDashboardLayoutAsync(CancellationToken ct = default)
    {
        var resp = await GetOrDefault<DashboardLayoutResponse>("api/v1/me/dashboard-layout", ct);
        return resp?.Layout;
    }

    public Task<bool> SaveDashboardLayoutAsync(string layoutJson, CancellationToken ct = default)
        => PostForOk("api/v1/me/dashboard-layout", new { layout = layoutJson }, ct);

    // ── Notifications (bulk + delete) ────────────────────────────────────────

    public Task MarkNotificationsBulkReadAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
        => PostJson<object, object>("api/v1/notifications/bulk-read", new { ids }, ct);

    public async Task DeleteNotificationAsync(long id, CancellationToken ct = default)
        => await SendJson<object>(HttpMethod.Delete, $"api/v1/notifications/{id}", null, ct);

    public Task DeleteNotificationsBulkAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
        => PostJson<object, object>("api/v1/notifications/bulk-delete", new { ids }, ct);

    // ── Web push ─────────────────────────────────────────────────────────────

    public Task<VapidKeyResponse?> PushVapidKeyAsync(CancellationToken ct = default)
        => GetOrDefault<VapidKeyResponse>("api/v1/push/vapid-key", ct);

    public Task<PushStatusResponse?> PushStatusAsync(CancellationToken ct = default)
        => GetOrDefault<PushStatusResponse>("api/v1/push/status", ct);

    public Task<bool> PushSubscribeAsync(PushSubscribeRequestDto request, CancellationToken ct = default)
        => PostForOk("api/v1/push/subscribe", request, ct);

    public Task<bool> PushUnsubscribeAsync(string endpoint, CancellationToken ct = default)
        => PostForOk("api/v1/push/unsubscribe", new PushUnsubscribeRequestDto { Endpoint = endpoint }, ct);

    private async Task<TResp?> PostJson<TReq, TResp>(string url, TReq body, CancellationToken ct)
    {
        try
        {
            EnsureBaseAddress();
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body),
            };
            if (!string.IsNullOrEmpty(_state.StreamConfig))
                req.Headers.TryAddWithoutValidation(ConfigHeader, _state.StreamConfig);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return default;
            if (typeof(TResp) == typeof(object)) return default;
            return await resp.Content.ReadFromJsonAsync<TResp>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST {Url} failed.", url);
            return default;
        }
    }

    // Generic verb sender (used for DELETE, optionally with a body). Mirrors
    // PostJson's degrade-to-default + config-header behaviour.
    private async Task<TResp?> SendJson<TResp>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        try
        {
            EnsureBaseAddress();
            using var req = new HttpRequestMessage(method, url);
            if (body is not null) req.Content = JsonContent.Create(body);
            if (!string.IsNullOrEmpty(_state.StreamConfig))
                req.Headers.TryAddWithoutValidation(ConfigHeader, _state.StreamConfig);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return default;
            if (typeof(TResp) == typeof(object)) return default;
            if (resp.Content.Headers.ContentLength == 0) return default;
            return await resp.Content.ReadFromJsonAsync<TResp>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Method} {Url} failed.", method, url);
            return default;
        }
    }

    // POST that only cares whether the call succeeded (push subscribe/unsubscribe).
    private async Task<bool> PostForOk<TReq>(string url, TReq body, CancellationToken ct)
    {
        try
        {
            EnsureBaseAddress();
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            if (!string.IsNullOrEmpty(_state.StreamConfig))
                req.Headers.TryAddWithoutValidation(ConfigHeader, _state.StreamConfig);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST {Url} failed.", url);
            return false;
        }
    }

    // Raw-JSON GET (config export: the body is downloaded as a file, so the client
    // keeps it opaque rather than modelling the full TokenData round-trip).
    private async Task<string?> GetRaw(string url, CancellationToken ct)
    {
        try
        {
            EnsureBaseAddress();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_state.StreamConfig))
                req.Headers.TryAddWithoutValidation(ConfigHeader, _state.StreamConfig);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "GET(raw) {Url} failed.", url); return null; }
    }

    // Raw-JSON POST (config import: forwards the uploaded backup verbatim).
    private async Task<TResp?> PostRaw<TResp>(string url, string json, CancellationToken ct)
    {
        try
        {
            EnsureBaseAddress();
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_state.StreamConfig))
                req.Headers.TryAddWithoutValidation(ConfigHeader, _state.StreamConfig);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return default;
            return await resp.Content.ReadFromJsonAsync<TResp>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "POST(raw) {Url} failed.", url); return default; }
    }

    private async Task<T?> GetOrDefault<T>(string url, CancellationToken ct)
    {
        try
        {
            EnsureBaseAddress();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // User-scoped endpoints need the config credential; harmless on public ones.
            if (!string.IsNullOrEmpty(_state.StreamConfig))
                req.Headers.TryAddWithoutValidation(ConfigHeader, _state.StreamConfig);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return default;
            return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET {Url} failed.", url);
            return default;
        }
    }
}
