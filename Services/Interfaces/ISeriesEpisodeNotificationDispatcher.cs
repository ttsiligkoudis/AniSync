namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Per-user series dispatch: reads one Trakt-connected user's just-aired
    /// series episodes (their "my shows" calendar intersected with their Watching
    /// + Planning lists, within the lookback window) and inserts idempotent
    /// notification rows + fires Web Push — the series analogue of
    /// <see cref="IEpisodeNotificationDispatcher"/> for anime. Returns the number
    /// of notifications actually created (after dedupe).
    /// </summary>
    public interface ISeriesEpisodeNotificationDispatcher
    {
        Task<int> DispatchForUserAsync(string uid, TimeSpan lookback, CancellationToken ct = default);
    }
}
