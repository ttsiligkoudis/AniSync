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

    private readonly HttpClient _http;
    private readonly AppState _state;
    private readonly ILogger<AniSyncApi> _logger;

    public AniSyncApi(HttpClient http, AppState state, ILogger<AniSyncApi> logger)
    {
        _http = http;
        _state = state;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SuggestMatch>> SuggestAsync(string title, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<SuggestMatch>();
        var resp = await GetOrDefault<SuggestResponse>(
            $"api/v1/suggest?title={Uri.EscapeDataString(title)}&limit={limit}", ct);
        return resp?.Matches ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> DiscoverAsync(string kind, string? genre = null, string? skip = null, CancellationToken ct = default)
    {
        var url = $"api/v1/discover/{Uri.EscapeDataString(kind)}";
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(genre)) q.Add($"genre={Uri.EscapeDataString(genre)}");
        if (!string.IsNullOrWhiteSpace(skip)) q.Add($"skip={Uri.EscapeDataString(skip)}");
        if (q.Count > 0) url += "?" + string.Join("&", q);
        var resp = await GetOrDefault<MetaListResponse>(url, ct);
        return resp?.Results ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> DiscoverVideoAsync(string type, string mode, string? skip = null, CancellationToken ct = default)
    {
        var url = $"api/v1/discover/video/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(mode)}";
        if (!string.IsNullOrWhiteSpace(skip)) url += $"?skip={Uri.EscapeDataString(skip)}";
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

    public async Task<IReadOnlyList<MetaDto>> ListAsync(string status, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<MetaListResponse>($"api/v1/me/list?status={Uri.EscapeDataString(status)}", ct);
        return resp?.Results ?? new();
    }

    public async Task<MetaDto?> AnimeAsync(string id, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<AnimeResponse>($"api/v1/anime/{Uri.EscapeDataString(id)}", ct);
        return resp?.Meta;
    }

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

    public async Task<AnimeEntryDto?> GetEntryAsync(string id, int? season = null, CancellationToken ct = default)
    {
        var url = $"api/v1/me/entries/{Uri.EscapeDataString(id)}";
        if (season.HasValue) url += $"?season={season.Value}";
        var resp = await GetOrDefault<EntryResponse>(url, ct);
        return resp?.Entry;
    }

    public async Task<SaveEntryResponse?> SaveEntryAsync(string id, EntrySaveRequest request, int? season = null, CancellationToken ct = default)
    {
        var url = $"api/v1/me/entries/{Uri.EscapeDataString(id)}";
        if (season.HasValue) url += $"?season={season.Value}";
        return await PostJson<EntrySaveRequest, SaveEntryResponse>(url, request, ct);
    }

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

    private async Task<TResp?> PostJson<TReq, TResp>(string url, TReq body, CancellationToken ct)
    {
        try
        {
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

    private async Task<T?> GetOrDefault<T>(string url, CancellationToken ct)
    {
        try
        {
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
