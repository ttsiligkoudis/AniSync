using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Per-user series-episode dispatch. Unlike the anime path — which matches a
    /// single global AniList airing against every user's cached watching list —
    /// series detection is naturally per-user: Trakt's <c>/calendars/my/shows</c>
    /// gives one user's shows' episodes in a window in a single call. We intersect
    /// that with the user's Watching (playback) + Planning (watchlist) series so we
    /// only notify for the scopes the user opted into (the calendar also includes
    /// collected-only shows), then create one notification per just-aired episode.
    /// <para>
    /// Reuses the anime delivery layer verbatim: <see cref="NotificationRecord"/>
    /// (with <c>Service = Trakt</c>, a numeric <c>Season</c>, and the imdb id as
    /// <c>AnimeId</c>), <see cref="INotificationStore.CreateAsync"/> for the bell row
    /// (idempotent on uid + id + season + episode, so overlapping lookback windows
    /// across passes are safe), and <see cref="IPushNotificationService"/> for Web
    /// Push. No cross-service equivalent ids exist for a series imdb id, so
    /// <c>equivalentAnimeIds</c> is null.
    /// </para>
    /// </summary>
    public class SeriesEpisodeNotificationDispatcher : ISeriesEpisodeNotificationDispatcher
    {
        // Calendar window: yesterday → +2 days, so a lookback that crosses UTC
        // midnight still sees episodes that aired late "yesterday" UTC.
        private const int CalendarDays = 2;

        private readonly ITraktService _trakt;
        private readonly INotificationStore _notifications;
        private readonly IPushNotificationService _push;
        private readonly ILogger<SeriesEpisodeNotificationDispatcher> _logger;

        public SeriesEpisodeNotificationDispatcher(
            ITraktService trakt,
            INotificationStore notifications,
            IPushNotificationService push,
            ILogger<SeriesEpisodeNotificationDispatcher> logger)
        {
            _trakt = trakt;
            _notifications = notifications;
            _push = push;
            _logger = logger;
        }

        public async Task<int> DispatchForUserAsync(string uid, TimeSpan lookback, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(uid)) return 0;

            // Skip users whose Trakt token is missing / needs reauth / unrefreshable —
            // GetValidTokenAsync returns null and the calendar read would no-op anyway.
            if (await _trakt.GetValidTokenAsync(uid) == null) return 0;

            // Eligible scope = Watching (playback) ∪ Planning (watchlist) series imdb ids.
            var planning = await _trakt.GetWatchlistAsync(uid);
            var watching = await _trakt.GetPlaybackAsync(uid);
            var eligible = planning.Concat(watching)
                .Where(i => i.Type == "series" && !string.IsNullOrEmpty(i.ImdbId))
                .Select(i => i.ImdbId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (eligible.Count == 0) return 0;

            var startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
            var calendar = await _trakt.GetMyShowsCalendarAsync(uid, startDate, CalendarDays);
            if (calendar.Count == 0) return 0;

            var now = DateTimeOffset.UtcNow;
            var windowStart = now - lookback;
            var createdAt = now.ToUnixTimeSeconds();

            var created = 0;
            foreach (var entry in calendar)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(entry.ImdbId) || !eligible.Contains(entry.ImdbId)) continue;
                // "Just aired": air instant within the lookback window. Entries
                // without a parseable air time can't be timed, so skip them.
                if (entry.FirstAired is not { } aired) continue;
                if (aired < windowStart || aired > now) continue;

                var record = new NotificationRecord
                {
                    Uid = uid,
                    Service = AnimeService.Trakt,
                    AnimeId = entry.ImdbId,
                    AnimeTitle = entry.ShowTitle,
                    EpisodeNumber = entry.Episode,
                    Season = entry.Season,
                    ThumbnailUrl = entry.ThumbnailUrl,
                    LinkPath = $"/meta/{entry.ImdbId}/watch/{entry.Season}/{entry.Episode}?type=series",
                    CreatedAt = createdAt,
                };

                // No cross-service equivalents for a series imdb id → null.
                var inserted = await _notifications.CreateAsync(record, equivalentAnimeIds: null);
                if (!inserted) continue;

                created++;
                // Web Push is best-effort; a dead provider can't break the bell row
                // that's already persisted (mirrors the anime dispatcher).
                try { await _push.SendAsync(uid, record); }
                catch (Exception ex) { _logger.LogWarning(ex, "Series Web Push send failed for {Uid}", uid); }
            }

            return created;
        }
    }
}
