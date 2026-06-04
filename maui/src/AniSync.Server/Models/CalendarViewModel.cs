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

    /// <summary>One day in the week — drives both the strip cell and (when it has episodes) a body section.</summary>
    public class CalendarDay
    {
        public DateOnly Date { get; set; }
        public string DateIso { get; set; }   // yyyy-MM-dd, used for the section anchor + day link
        public string Label { get; set; }     // e.g. "Friday, May 29th" — the section heading
        public bool IsToday { get; set; }
        public bool IsSelected { get; set; }
        public bool HasAnime { get; set; }
        public bool HasSeries { get; set; }
        public List<CalendarEpisode> Episodes { get; set; } = new();
    }

    /// <summary>
    /// Backing model for the Calendar page: a Sunday-first week strip to pick a day,
    /// and the whole week's episodes below — one section per day-with-episodes, each
    /// a horizontal-scroll list of cards.
    /// </summary>
    public class CalendarViewModel
    {
        /// <summary>The 7-day strip, Sunday → Saturday (also the body sections).</summary>
        public List<CalendarDay> Days { get; set; } = new();

        /// <summary>yyyy-MM-dd of the selected day (strip highlight + scroll target).</summary>
        public string SelectedDateIso { get; set; }

        /// <summary>True when a ?d= was supplied (a day was tapped) — scroll its section into view.</summary>
        public bool ScrollToSelected { get; set; }

        /// <summary>yyyy-MM-dd anchors for the prev/next week links (same weekday, ±7 days).</summary>
        public string PrevDate { get; set; }
        public string NextDate { get; set; }

        public bool SelectedIsToday { get; set; }

        /// <summary>Episode count across the whole visible week.</summary>
        public int TotalEpisodes { get; set; }
    }
}
