namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Orchestrates a single tick of "find newly-aired episodes and create
    /// notifications for users tracking them". Called from
    /// <c>POST /api/v1/cron/check-releases</c>, which the
    /// <c>cf-episode-notifier</c> Cloudflare Worker hits every 5 minutes.
    /// </summary>
    public interface IEpisodeNotificationDispatcher
    {
        Task<DispatchResult> RunAsync(CancellationToken ct = default);
    }

    public record DispatchResult(
        int CachesRefreshed,
        int CachesFailed,
        int AiringChecked,
        int NotificationsCreated,
        int NotificationsSuppressed);
}
