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
        /// <summary>Series only: count of distinct watched episodes in history.</summary>
        public int WatchedEpisodes { get; set; }
        /// <summary>The user's Trakt rating (1-10), or null when unrated.</summary>
        public int? Rating { get; set; }
    }
}
