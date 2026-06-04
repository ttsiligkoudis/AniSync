namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Looks up filler / canon classification for an anime's episodes from the
    /// AnimeFillerList community site. Long-running shounen (Naruto, Bleach, One Piece)
    /// have huge filler arcs the meta UI can mark with a 🟨 prefix so users can skip
    /// them at a glance; canon episodes get 🟦, mixed get 🟧.
    /// </summary>
    public interface IFillerListService
    {
        /// <summary>
        /// Returns episode-number → category for the named show, or an empty dictionary
        /// when AnimeFillerList doesn't recognise the title or the request fails. Best-
        /// effort by design — every caller (the meta page enrichment) treats a missing
        /// dict as "skip the labels for this show". Categories are normalised to one of
        /// <c>"canon"</c>, <c>"filler"</c>, <c>"mixed"</c>.
        /// </summary>
        Task<Dictionary<int, string>> GetEpisodeCategoriesAsync(string animeName);
    }
}
