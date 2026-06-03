using System.Net.Http.Json;
using AniSync.Client.Models;
using Microsoft.Extensions.Logging;

namespace AniSync.Client.Services;

/// <summary>
/// Default <see cref="IAniSyncApi"/> implementation. Registered as a typed
/// HttpClient in each head's DI; the head sets BaseAddress to the backend URL
/// (and, on MAUI, attaches the auth token / cookie handler).
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
        try
        {
            var url = $"api/v1/suggest?title={Uri.EscapeDataString(title)}&limit={limit}";
            var resp = await _http.GetFromJsonAsync<SuggestResponse>(url, ct);
            return resp?.Matches ?? new List<SuggestMatch>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Suggest failed (title={Title}).", title);
            return Array.Empty<SuggestMatch>();
        }
    }

    public async Task<IReadOnlyList<MetaCard>> DiscoverAsync(string kind, string? genre = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/discover/{Uri.EscapeDataString(kind)}";
            if (!string.IsNullOrWhiteSpace(genre)) url += $"?genre={Uri.EscapeDataString(genre)}";
            // The discover endpoint returns a MetaListResponse { metas: [...] };
            // tolerate either a bare array or the wrapped shape.
            var wrapped = await _http.GetFromJsonAsync<MetaListResponse>(url, ct);
            return wrapped?.Metas ?? new List<MetaCard>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discover failed (kind={Kind}).", kind);
            return Array.Empty<MetaCard>();
        }
    }

    private sealed class MetaListResponse
    {
        public List<MetaCard> Metas { get; set; } = new();
    }
}
