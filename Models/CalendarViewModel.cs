namespace AnimeList.Models
{
    /// <summary>
    /// One episode entry on the Calendar — either an anime episode (AniList airing
    /// schedule, matched to the user's Watching list) or a series episode (the
    /// user's Trakt Watching/Planning calendar).
    /// </summary>
    public record CalendarEpisode(
        string Kind,                // "anime" | "series"
        string Title,
        int? Season,                // null for single-cour anime; numeric for series
        int Episode,
        long AiringAt,              // Unix seconds, UTC
        string CoverImage,
        string LinkPath,
        string EpisodeTitle = null); // series episode title, when Trakt has one

    /// <summary>One day in the week-strip picker.</summary>
    public class CalendarDay
    {
        public DateOnly Date { get; set; }
        public bool IsToday { get; set; }
        public bool IsSelected { get; set; }
        public bool HasAnime { get; set; }
        public bool HasSeries { get; set; }
        public List<CalendarEpisode> Episodes { get; set; } = new();
    }

    /// <summary>
    /// Backing model for the Calendar page: a Sunday-first week strip to pick a day,
    /// plus the selected day's episodes rendered as cards.
    /// </summary>
    public class CalendarViewModel
    {
        /// <summary>The 7-day strip, Sunday → Saturday.</summary>
        public List<CalendarDay> Days { get; set; } = new();

        public DateOnly SelectedDate { get; set; }
        public string SelectedDateLabel { get; set; }   // e.g. "June 3rd, 2026"
        public List<CalendarEpisode> SelectedEpisodes { get; set; } = new();

        /// <summary>yyyy-MM-dd anchors for the prev/next week links (same weekday, ±7 days).</summary>
        public string PrevDate { get; set; }
        public string NextDate { get; set; }

        public bool SelectedIsToday { get; set; }

        /// <summary>Episode count across the whole visible week.</summary>
        public int TotalEpisodes { get; set; }
    }
}
