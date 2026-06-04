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

        // The IMDb-side season number for this cour. Anime franchises
        // share one IMDb listing across cours and each cour gets its
        // own AniList / MAL id paired with a "season N" pointer back
        // into that listing. Torrentio addresses series by
        // tt{imdb}:{s}:{e}, so without this we'd request season 1 of
        // the franchise for every cour and get the wrong episodes
        // back (Re:Zero S4E3 silently turning into S1E3 was the
        // symptom that surfaced this).
        public int? ImdbSeason { get; set; }
    }
}
