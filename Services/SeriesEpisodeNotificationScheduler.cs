using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Drives series-episode notifications on an hourly cadence. The anime path is
    /// fed by a global AniList schedule that's cheap to re-walk on every cron ping,
    /// so it dispatches per-minute; series detection instead costs ~3 Trakt calls
    /// per connected user (calendar + watchlist + playback), so it must run on a
    /// coarse interval rather than off <c>POST /cron/check-releases</c>.
    /// <para>
    /// Each pass enumerates Trakt-connected users and hands each to
    /// <see cref="ISeriesEpisodeNotificationDispatcher"/> with a lookback slightly
    /// wider than the interval (drift tolerance). Overlapping windows are safe —
    /// <see cref="INotificationStore.CreateAsync"/>'s unique index makes a
    /// re-dispatched episode a no-op. Users are processed in bounded batches so a
    /// large install doesn't fan out to Trakt all at once.
    /// </para>
    /// </summary>
    public class SeriesEpisodeNotificationScheduler : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        // Lookback > Interval so an episode airing right after a pass is still
        // inside the window on the next pass; the dedupe index absorbs the overlap.
        private static readonly TimeSpan Lookback = TimeSpan.FromMinutes(70);

        // Let the app warm up before the first fan-out after a (re)deploy.
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

        // Bound the per-pass Trakt fan-out; a short pause between batches spreads load.
        private const int BatchSize = 25;
        private static readonly TimeSpan InterBatchDelay = TimeSpan.FromSeconds(2);

        private readonly IServiceProvider _services;
        private readonly ILogger<SeriesEpisodeNotificationScheduler> _logger;

        public SeriesEpisodeNotificationScheduler(
            IServiceProvider services,
            ILogger<SeriesEpisodeNotificationScheduler> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            await RunPassAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    await RunPassAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }

        private async Task RunPassAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _services.CreateScope();
                var trakt = scope.ServiceProvider.GetRequiredService<ITraktService>();
                if (!trakt.IsConfigured) return;

                var configStore = scope.ServiceProvider.GetRequiredService<IConfigStore>();
                var dispatcher = scope.ServiceProvider.GetRequiredService<ISeriesEpisodeNotificationDispatcher>();

                var uids = await configStore.ListTraktConnectedUidsAsync();
                if (uids.Count == 0) return;

                var created = 0;
                for (var i = 0; i < uids.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        created += await dispatcher.DispatchForUserAsync(uids[i], Lookback, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "series dispatch failed for {Uid}", uids[i]);
                    }

                    // Pause between batches to avoid a simultaneous Trakt burst.
                    if ((i + 1) % BatchSize == 0 && i + 1 < uids.Count)
                    {
                        try { await Task.Delay(InterBatchDelay, ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }

                _logger.LogInformation(
                    "series notifier pass: {Users} Trakt users, {Created} new notifications",
                    uids.Count, created);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "series notifier RunPassAsync failed");
            }
        }
    }
}
