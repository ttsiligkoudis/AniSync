namespace AnimeList.Models
{
    /// <summary>
    /// One just-aired (or upcoming) episode from a user's Trakt "my shows"
    /// calendar (<c>/calendars/my/shows</c>). Carries the parent show's IMDb id
    /// so it can be matched against the user's Watching / Planning lists, plus the
    /// air instant (UTC) the series-episode notifier uses for "just aired" timing.
    /// </summary>
    public record TraktCalendarEpisode
    {
        /// <summary>tt-prefixed IMDb id of the parent show. Never null for usable entries.</summary>
        public string ImdbId { get; init; }

        public string ShowTitle { get; init; }
        public int Season { get; init; }
        public int Episode { get; init; }
        public string EpisodeTitle { get; init; }

        /// <summary>Air instant in UTC; null when Trakt's first_aired was missing/unparseable.</summary>
        public DateTimeOffset? FirstAired { get; init; }

        /// <summary>Episode screenshot, falling back to the show poster; may be null.</summary>
        public string ThumbnailUrl { get; init; }
    }
}
