namespace AnimeList.Models
{
    /// <summary>
    /// View model for Views/Library/_LibraryPane.cshtml — the shared partial
    /// that renders the poster grid for both the initial /library render
    /// and /library/page's filter-search.js pane-swap. Just wraps the
    /// grid view-model; kept as its own type so the partial signature
    /// can grow without churning every call site.
    /// </summary>
    public class LibraryPaneViewModel
    {
        public PosterGridViewModel Grid { get; set; }
    }
}
