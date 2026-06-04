namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Public surface for the in-process <c>EpisodeNotificationScheduler</c>
    /// background service so external triggers (notably the
    /// <c>cf-episode-notifier</c> Cloudflare Worker pinging
    /// <c>POST /api/v1/cron/check-releases</c>) can request an immediate
    /// schedule refresh + dispatch pass without waiting for the next
    /// daily UTC-midnight tick.
    /// </summary>
    public interface IEpisodeNotificationScheduler
    {
        /// <summary>
        /// Runs the same schedule refresh + arm-future-timers +
        /// dispatch-past-episodes pass the background service runs at
        /// startup and at UTC midnight. Idempotent — already-armed
        /// timers and already-dispatched episodes are no-ops via the
        /// in-memory dedup set and the notification store's UNIQUE
        /// INDEX.
        /// </summary>
        Task TriggerRefreshAsync(CancellationToken ct = default);
    }
}
