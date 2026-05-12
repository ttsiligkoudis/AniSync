namespace AnimeList.Models
{
    /// <summary>
    /// Slim studio entry — id + display name — surfaced by the
    /// /studio listing page. Per-studio detail is fetched separately
    /// by <see cref="Services.Interfaces.IAnilistFallback.GetStudioMediaAsync"/>
    /// when the user opens a tile, so this shape stays minimal on
    /// purpose: anything richer (anime counts, logo, etc.) would
    /// require a per-studio round-trip that AniList's Page.studios
    /// query doesn't return inline.
    /// </summary>
    public class StudioSummary
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
