namespace AnimeList.Models
{
    /// <summary>
    /// One episode cell-entry on the Calendar page — either an anime episode
    /// (from the AniList airing schedule, matched to the user's Watching list)
    /// or a series episode (from the user's Trakt Watching/Planning calendar).
    /// </summary>
    public record CalendarEpisode(
        string Kind,            // "anime" | "series"
        string Title,
        int? Season,            // null for single-cour anime; numeric for series
        int Episode,
        long AiringAt,          // Unix seconds, UTC
        string CoverImage,
        string LinkPath);

    /// <summary>One cell in the month grid (row-major, Sunday-first).</summary>
    public class CalendarDay
    {
        public DateOnly Date { get; set; }
        public bool InMonth { get; set; }
        public bool IsToday { get; set; }
        public List<CalendarEpisode> Episodes { get; set; } = new();
    }

    /// <summary>Backing model for the month-grid Calendar page.</summary>
    public class CalendarViewModel
    {
        public int Year { get; set; }
        public int Month { get; set; }          // 1-12
        public string MonthLabel { get; set; }  // e.g. "June 2026"

        public int PrevYear { get; set; }
        public int PrevMonth { get; set; }
        public int NextYear { get; set; }
        public int NextMonth { get; set; }

        /// <summary>True when the rendered month is the current UTC month (hides the "Today" jump).</summary>
        public bool IsCurrentMonth { get; set; }

        /// <summary>Full grid, row-major Sunday-first — always a multiple of 7 cells.</summary>
        public List<CalendarDay> Days { get; set; } = new();

        public int TotalEpisodes { get; set; }

        public static readonly string[] WeekdayLabels = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    }
}
