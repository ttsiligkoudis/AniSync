namespace AnimeList.Models
{
    /// <summary>
    /// View model for Views/Library/_LibraryPane.cshtml — the shared partial
    /// that renders either a paginator-wrapped grid (non-search list view with
    /// more results upstream) or a plain grid (search results / non-paginated
    /// list). Used by both the initial /library render and /library/page's
    /// fullPane swap so the swapped-in pane keeps infinite-scroll behaviour.
    /// </summary>
    public class LibraryPaneViewModel
    {
        public PosterGridViewModel Grid { get; set; }
        public bool ShowPaginator { get; set; }
        public string ListSlug { get; set; }
        public string Search { get; set; }
        public string Genre { get; set; }
        public int Skip { get; set; }
    }
}
