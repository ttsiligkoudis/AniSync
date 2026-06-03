using AniSync.Client.Models;

namespace AniSync.Client.Services;

/// <summary>
/// Thin client over the AniSync backend's JSON API (/api/v1). One typed
/// surface the shared components call; the concrete <see cref="AniSyncApi"/>
/// wraps an HttpClient whose BaseAddress is set per-head from
/// <see cref="IAppEnvironment.ApiBaseUrl"/>.
///
/// Endpoints that already exist on the server are implemented today; the ones
/// the video/Trakt surfaces need (library, shelves, stats as JSON) are listed
/// in maui/README.md under "API surface to add" and will be filled in as those
/// screens are ported.
/// </summary>
public interface IAniSyncApi
{
    /// <summary>Search typeahead — GET /api/v1/suggest?title=&amp;limit=.</summary>
    Task<IReadOnlyList<SuggestMatch>> SuggestAsync(string title, int limit = 8, CancellationToken ct = default);

    /// <summary>Discovery catalog — GET /api/v1/discover/{kind} (trending/seasonal/airing).</summary>
    Task<IReadOnlyList<MetaCard>> DiscoverAsync(string kind, string? genre = null, CancellationToken ct = default);
}
