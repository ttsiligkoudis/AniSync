namespace AnimeList.Models
{
    /// <summary>
    /// Represents the user's list entry for an anime.
    /// </summary>
    public class AnimeEntry
    {
        public string EntryId { get; set; }
        public string MediaId { get; set; }
        public string Status { get; set; }
        public int Progress { get; set; }
        public int? TotalEpisodes { get; set; }

        /// <summary>
        /// Score in the user's native scale.
        /// AniList: 0–100 / 0.0–10.0 / 0–5 depending on the user's profile setting.
        /// Kitsu: 0.0–10.0 (translated from <c>ratingTwenty</c> at the service boundary).
        /// </summary>
        public double? Score { get; set; }

        /// <summary>Free-form private notes the user has on the entry.</summary>
        public string Notes { get; set; }

        /// <summary>Rewatch / reconsume count.</summary>
        public int RewatchCount { get; set; }

        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }
}
