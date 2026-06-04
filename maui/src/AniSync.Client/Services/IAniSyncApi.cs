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
    Task<IReadOnlyList<MetaDto>> DiscoverAsync(string kind, string? genre = null, CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> AiringTodayAsync(CancellationToken ct = default);

    // Dashboard (user-scoped)
    Task<AnilistUserStatsDto?> AnilistStatsAsync(CancellationToken ct = default);
    Task<TraktUserStatsDto?> TraktStatsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MetaDto>> ContinueWatchingAsync(int limit = 15, CancellationToken ct = default);

    // Detail + watch
    Task<MetaDto?> AnimeAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<StreamingLinkDto>> StreamsAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<EpisodeInfoDto>> EpisodesAsync(string id, CancellationToken ct = default);
}
