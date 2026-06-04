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

        // Layout shape. "grid" (default) wraps to a CSS-grid auto-fill layout
        // — used by Library / Discover / search results. "scroll" renders a
        // horizontal flex row with overflow-x for the StreamD-style "Continue
        // watching" / shelf surfaces on the dashboard. Empty/null defaults to
        // grid in the partial so existing call sites stay intact.
        public string Variant { get; set; }

        // When true, cards link into the video section (/movie/{id} or
        // /series/{id}, chosen per item from Meta.type) instead of the default
        // anime detail page (/anime/{id}). Set by the VideoController browse +
        // pagination paths so Cinemeta cards route to the video detail page.
        public bool VideoLinks { get; set; }
    }
}
