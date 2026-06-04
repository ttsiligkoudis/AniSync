using System.Net.Http.Json;
using AniSync.Client.Models;
using Microsoft.Extensions.Logging;

namespace AniSync.Client.Services;

/// <summary>
/// Default <see cref="IAniSyncApi"/> implementation. Registered as a typed
/// HttpClient per head; the head sets BaseAddress to the backend URL (and, on
/// MAUI, attaches the auth token / cookie handler). Every call degrades to an
/// empty/null result on failure so a single dead shelf never breaks the page.
/// </summary>
public sealed class AniSyncApi : IAniSyncApi
{
    private readonly HttpClient _http;
    private readonly ILogger<AniSyncApi> _logger;

    public AniSyncApi(HttpClient http, ILogger<AniSyncApi> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SuggestMatch>> SuggestAsync(string title, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return Array.Empty<SuggestMatch>();
        var resp = await GetOrDefault<SuggestResponse>(
            $"api/v1/suggest?title={Uri.EscapeDataString(title)}&limit={limit}", ct);
        return resp?.Matches ?? new();
    }

    public async Task<IReadOnlyList<MetaDto>> DiscoverAsync(string kind, string? genre = null, CancellationToken ct = default)
    {
        var url = $"api/v1/discover/{Uri.EscapeDataString(kind)}";
        if (!string.IsNullOrWhiteSpace(genre)) url += $"?genre={Uri.EscapeDataString(genre)}";
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
        // Trakt stats are only exposed by the MVC JSON action, not /api/v1.
        var resp = await GetOrDefault<TraktStatsEnvelope>("Home/TraktStatsData", ct);
        return resp is { Success: true } ? resp.Stats : null;
    }

    public async Task<IReadOnlyList<MetaDto>> ContinueWatchingAsync(int limit = 15, CancellationToken ct = default)
    {
        var resp = await GetOrDefault<ContinueWatchingResponse>($"api/v1/me/continue-watching?limit={limit}", ct);
        return resp?.Items ?? new();
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

    public async Task<IReadOnlyList<StremioStream>> PlaybackSourcesAsync(string config, string type, string streamId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config)) return Array.Empty<StremioStream>();
        var url = $"{config.Trim('/')}/stream/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(streamId)}.json";
        var resp = await GetOrDefault<StremioStreamsResponse>(url, ct);
        // Only direct-URL sources are playable in a thin client.
        return resp?.Streams.Where(s => !string.IsNullOrEmpty(s.Url)).ToList() ?? new();
    }

    private async Task<T?> GetOrDefault<T>(string url, CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<T>(url, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET {Url} failed.", url);
            return default;
        }
    }
}
