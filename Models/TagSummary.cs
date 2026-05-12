namespace AnimeList.Models
{
    /// <summary>
    /// Slim tag entry — name + category + description — surfaced by the
    /// /discover/tag listing page. AniList's MediaTagCollection returns
    /// the entire tag set in a single un-paginated call (a few hundred
    /// entries), so the listing renders all of them at once with no
    /// infinite-scroll machinery. Per-tag detail is fetched separately
    /// via <see cref="Services.Interfaces.IAnilistFallback.GetByTagPageAsync"/>
    /// when the user opens a tile. Category lets the view group tiles
    /// under section headers (Theme, Setting, Demographic, …) so the
    /// dense tag space stays scannable.
    /// </summary>
    public class TagSummary
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
    }
}
