namespace AnimeList.Models
{
    /// <summary>
    /// Payload for the shared <c>_PosterGrid</c> partial. The Library, Discover, and
    /// (eventually) per-list-type tab pages all render the same poster grid; this
    /// small DTO is what they hand the partial. ConfigUid is optional — when null
    /// (anonymous Discover) the partial renders inert cards instead of links to the
    /// config-scoped Manage Entry page.
    /// </summary>
    public class PosterGridViewModel
    {
        public List<Meta> Items { get; set; } = [];
        public string ConfigUid { get; set; }
        public string EmptyMessage { get; set; }
    }
}
