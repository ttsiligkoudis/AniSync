namespace AnimeList.Models
{
    /// <summary>
    /// One row in the notifications table — a per-user record that an
    /// anime episode has been (or is about to be) released. Rendered in
    /// the bell dropdown in the site header.
    /// </summary>
    public class NotificationRecord
    {
        public long Id { get; set; }
        public string Uid { get; set; }
        public AnimeService Service { get; set; }

        /// <summary>Prefixed media id ready for /anime/{id} routing (anilist:N / mal:N / kitsu:N).</summary>
        public string AnimeId { get; set; }

        public string AnimeTitle { get; set; }
        public int EpisodeNumber { get; set; }

        /// <summary>Null for single-cour shows; numeric for multi-season franchises.</summary>
        public int? Season { get; set; }

        public string ThumbnailUrl { get; set; }

        /// <summary>Pre-baked deep link the bell anchors at, e.g. /anime/anilist:123/watch/5.</summary>
        public string LinkPath { get; set; }

        /// <summary>Unix seconds.</summary>
        public long CreatedAt { get; set; }

        /// <summary>Unix seconds when the user opened the notification, or null while still unread.</summary>
        public long? ReadAt { get; set; }
    }
}
