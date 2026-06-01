namespace AnimeList.Models
{
    /// <summary>
    /// One episode entry on the Calendar — either an anime episode (AniList airing
    /// schedule, matched to the user's Watching list) or a series episode (the
    /// user's Trakt Watching/Planning calendar).
    /// </summary>
    public record CalendarEpisode(
        string Kind,            // "anime" | "series"
        string Title,
        int? Season,            // null for single-cour anime; numeric for series
        int Episode,
        long AiringAt,          // Unix seconds, UTC
        string CoverImage,
        string LinkPath);

    /// <summary>One day in the week view, with its episodes.</summary>
    public class CalendarDay
    {
        public DateOnly Date { get; set; }
        public bool IsToday { get; set; }
        public List<CalendarEpisode> Episodes { get; set; } = new();
    }

    /// <summary>Backing model for the weekly Calendar page (Sunday-first, 7 days).</summary>
    public class CalendarViewModel
    {
        public DateOnly WeekStart { get; set; }      // Sunday
        public DateOnly WeekEnd { get; set; }        // Saturday (inclusive)
        public string RangeLabel { get; set; }       // e.g. "Jun 1 – 7, 2026"

        /// <summary>yyyy-MM-dd anchors for the prev/next week links.</summary>
        public string PrevDate { get; set; }
        public string NextDate { get; set; }

        /// <summary>True when the rendered week contains today (hides the "This week" jump).</summary>
        public bool IsCurrentWeek { get; set; }

        /// <summary>Always 7 entries, Sunday → Saturday.</summary>
        public List<CalendarDay> Days { get; set; } = new();

        public int TotalEpisodes { get; set; }
    }
}
