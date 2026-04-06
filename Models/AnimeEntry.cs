namespace AnimeList.Models
{
    /// <summary>
    /// Represents the user's list entry for an anime (status, progress).
    /// </summary>
    public class AnimeEntry
    {
        public string EntryId { get; set; }
        public string MediaId { get; set; }
        public string Status { get; set; }
        public int Progress { get; set; }
        public int? TotalEpisodes { get; set; }
    }
}
