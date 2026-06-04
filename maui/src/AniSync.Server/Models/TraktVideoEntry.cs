namespace AnimeList.Models
{
    /// <summary>
    /// Aggregate Trakt state for a single movie / series, assembled for the
    /// Manage Entry modal on the meta detail page. Trakt has no single "entry"
    /// object like the anime trackers, so this folds the three relevant
    /// surfaces — watchlist membership, watched history, and the user's rating —
    /// into one shape the modal can render. The controller derives the modal's
    /// status string (planning / watching / completed) from these bits.
    /// </summary>
    public class TraktVideoEntry
    {
        /// <summary>The title is on the user's Trakt watchlist.</summary>
        public bool InWatchlist { get; set; }
        /// <summary>Movie only: the movie is in the user's watched history.</summary>
        public bool Watched { get; set; }
        /// <summary>The title has an in-progress playback (continue-watching) — a
        /// part-watched movie or a series with an active episode position. The
        /// "watching" signal for both types.</summary>
        public bool InPlayback { get; set; }
        /// <summary>Series only: count of distinct watched episodes in history.</summary>
        public int WatchedEpisodes { get; set; }
        /// <summary>
        /// Set when the title sits in one of the AniSync-managed Trakt personal
        /// lists that back the statuses Trakt has no native surface for —
        /// "onhold" / "dropped" / "rewatching". Null otherwise. Takes precedence
        /// over the watchlist/playback/history-derived status.
        /// </summary>
        public string CustomStatus { get; set; }
        /// <summary>The user's Trakt rating (1-10), or null when unrated.</summary>
        public int? Rating { get; set; }
    }
}
