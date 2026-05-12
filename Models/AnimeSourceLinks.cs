namespace AnimeList.Models
{
    /// <summary>
    /// Resolved external-site identifiers for an anime — surfaces the
    /// per-service source chips on the detail page and feeds the
    /// Torrentio / RD path on the streams endpoints. All fields nullable;
    /// null means "no mapping found, don't render that link".
    /// </summary>
    public class AnimeSourceLinks
    {
        public int? AnilistId { get; set; }
        public int? MalId { get; set; }
        public int? KitsuId { get; set; }
        public string ImdbId { get; set; }
    }
}
