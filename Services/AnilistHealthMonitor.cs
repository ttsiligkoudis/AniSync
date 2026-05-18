namespace AnimeList.Services
{
    // Surfaces a transient "AniList is down" signal so the layout can render
    // a banner and catalog reads can fall back to Kitsu when the user's
    // primary service is AniList. Tracks the most recent failure as a UTC
    // tick timestamp; IsDown stays true until either the window elapses or
    // a successful call clears it. Only HTTP-level failures (5xx, network,
    // timeout) feed this — auth / not-found / rate-limit don't trip it.
    public static class AnilistHealthMonitor
    {
        // How long after a failure we keep claiming AniList is down even if
        // no further calls fire. Five minutes is long enough to bridge a
        // typical short blip without making the banner stick around once the
        // upstream is back; the very next successful call clears it anyway.
        private const int OutageWindowMinutes = 5;

        private static long _lastFailureTicks;

        public static void RecordFailure()
        {
            Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
        }

        public static void RecordSuccess()
        {
            Interlocked.Exchange(ref _lastFailureTicks, 0);
        }

        public static bool IsDown
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastFailureTicks);
                if (ticks == 0) return false;
                var failureTime = new DateTime(ticks, DateTimeKind.Utc);
                return (DateTime.UtcNow - failureTime).TotalMinutes < OutageWindowMinutes;
            }
        }
    }
}
