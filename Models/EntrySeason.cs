namespace AnimeList.Models
{
    /// <summary>
    /// One option in the Manage Entry "Season" dropdown. For most ids (anilist:N, kitsu:N)
    /// there is exactly one season — the original id itself. For IMDb / TMDB ids that span
    /// multiple anime entries (e.g. a franchise split into cours like Spy x Family), each
    /// entry becomes its own EntrySeason carrying the resolved per-entry id so save/fetch
    /// can bypass the imdb→service mapping and hit the exact entry the user picked.
    /// </summary>
    public class EntrySeason
    {
        /// <summary>Service-prefixed id, e.g. "anilist:140960" or "kitsu:42857".</summary>
        public string Id { get; set; }

        /// <summary>Display label, typically "&lt;anime title&gt; (&lt;n&gt; ep)".</summary>
        public string Label { get; set; }

        /// <summary>Episode count for the dropdown to surface in the label and progress max.</summary>
        public int? TotalEpisodes { get; set; }
    }
}
