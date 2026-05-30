namespace AnimeList.Models
{
    /// <summary>
    /// A single Trakt list/playback entry, flattened to the bits the video
    /// section needs to render a poster row and a watch/detail link. Trakt
    /// returns movies and shows (with optional episode coordinates for
    /// playback), all of which we key off the IMDb id since that's what
    /// Cinemeta and the video routes use.
    /// </summary>
    public class TraktListItem
    {
        public string Type { get; set; }      // "movie" | "series"
        public string ImdbId { get; set; }    // tt-prefixed; null when Trakt has no imdb id
        public string Title { get; set; }
        public int? Year { get; set; }

        // Populated for in-progress episodes (continue-watching) only.
        public int? Season { get; set; }
        public int? Episode { get; set; }

        // 0–100 playback percent for continue-watching entries; null otherwise.
        public double? Progress { get; set; }

        // When this entry was paused / last interacted with — drives recency
        // ordering of the continue-watching row.
        public DateTime? PausedAt { get; set; }
    }
}
