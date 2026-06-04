namespace AnimeList.Models
{
    /// <summary>
    /// One opening/ending/recap/preview marker for a single anime episode, sourced from
    /// the AniSkip community API. Times are seconds from the start of the file.
    /// </summary>
    public class SkipTime
    {
        /// <summary>"op", "ed", "mixed-op", "mixed-ed", "recap", or "preview".</summary>
        public string Type { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
    }
}
