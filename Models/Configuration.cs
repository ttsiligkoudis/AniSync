namespace AnimeList.Models
{
    public class Configuration
    {
        public string tokenData  { get; set; }
        public bool showCurrent { get; set; }
        public bool showCompleted { get; set; }
        public bool showTrending { get; set; }
        public bool showSeasonal { get; set; }
        public bool showPlanning { get; set; }
        public bool showPaused { get; set; }
        public bool showDropped { get; set; }
        public bool showRepeating { get; set; }
        public bool showAiring { get; set; }
        public bool discoverOnlyCurrent { get; set; }
        public bool discoverOnlyCompleted { get; set; }
        public bool discoverOnlyTrending { get; set; }
        public bool discoverOnlySeasonal { get; set; }
        public bool discoverOnlyPlanning { get; set; }
        public bool discoverOnlyPaused { get; set; }
        public bool discoverOnlyDropped { get; set; }
        public bool discoverOnlyRepeating { get; set; }
        public bool discoverOnlyAiring { get; set; }
        public bool showExternalStreams { get; set; }
    }
}
