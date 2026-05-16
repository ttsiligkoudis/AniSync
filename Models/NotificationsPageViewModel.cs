namespace AnimeList.Models
{
    /// <summary>
    /// Strongly-typed payload for the /notifications page. The page renders
    /// the first chunk server-side and seeds the infinite-scroll paginator
    /// with the cursor (<see cref="NextSkip"/>) the JS needs to ask for the
    /// next chunk via /notifications/page?skip=N.
    /// </summary>
    public class NotificationsPageViewModel
    {
        public List<NotificationRecord> Items { get; set; } = [];

        /// <summary>The skip offset to pass on the next /notifications/page fetch.</summary>
        public int NextSkip { get; set; }

        /// <summary>
        /// True when the initial chunk filled the page size — there might
        /// be more to fetch. False when we already hit the end on the
        /// first render, so the page can omit the load-more sentinel.
        /// </summary>
        public bool HasMore { get; set; }

        /// <summary>Chunk size the paginator's data-page-size attribute mirrors.</summary>
        public int PageSize { get; set; }
    }
}
