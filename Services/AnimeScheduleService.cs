using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Singleton in-memory snapshot of the next 48h of anime airings, sourced
    /// from <see cref="IAnilistFallback.GetUpcomingEpisodesAsync"/>. The 48h
    /// window accommodates a UTC-midnight refresh that runs a bit late + the
    /// "what's the next airing right after midnight" lookup the bell makes
    /// at 00:00:30.
    /// </summary>
    public class AnimeScheduleService : IAnimeScheduleService
    {
        // Window the scheduler walks per refresh. Wide enough that even with
        // late-firing daily refreshes we always have tomorrow's morning
        // airings queued before they happen.
        private static readonly TimeSpan LookbackWindow = TimeSpan.FromHours(1);
        private static readonly TimeSpan LookaheadWindow = TimeSpan.FromHours(48);

        // IServiceProvider rather than a direct IAnilistFallback so we can
        // create a per-call scope — the fallback is registered scoped and
        // shouldn't be captured by this singleton.
        private readonly IServiceProvider _services;
        private readonly ILogger<AnimeScheduleService> _logger;

        // Volatile-replaced reference; reads always see either the old or
        // new snapshot atomically (List<T> reference assignment is atomic
        // on the CLR memory model, no torn reads).
        private IReadOnlyList<UpcomingEpisode> _schedule = [];
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public AnimeScheduleService(IServiceProvider services, ILogger<AnimeScheduleService> logger)
        {
            _services = services;
            _logger = logger;
        }

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            // Serialise concurrent refreshes (e.g. startup + first user click)
            // so we only hit AniList once for the same window.
            await _refreshLock.WaitAsync(ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var start = (now - LookbackWindow).ToUnixTimeSeconds();
                var end = (now + LookaheadWindow).ToUnixTimeSeconds();

                using var scope = _services.CreateScope();
                var anilist = scope.ServiceProvider.GetRequiredService<IAnilistFallback>();
                var fresh = await anilist.GetUpcomingEpisodesAsync(start, end);
                _schedule = fresh ?? [];
                _logger.LogInformation(
                    "AnimeScheduleService refreshed: {Count} airings in [{Start}, {End}]",
                    _schedule.Count, start, end);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AnimeScheduleService.RefreshAsync failed; keeping previous snapshot");
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        public IReadOnlyList<UpcomingEpisode> GetSchedule() => _schedule;

        public long? GetNextAiringAt()
        {
            var snapshot = _schedule;
            if (snapshot.Count == 0) return null;
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long? min = null;
            foreach (var e in snapshot)
            {
                if (e.AiringAt <= nowUnix) continue;
                if (min == null || e.AiringAt < min.Value) min = e.AiringAt;
            }
            return min;
        }
    }
}
