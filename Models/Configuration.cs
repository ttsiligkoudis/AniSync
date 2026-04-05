namespace AnimeList.Models
{
    public class Configuration
    {
        public string tokenData  { get; set; }
        public bool showCurrent { get; set; }
        public bool showCompleted { get; set; }
        public bool showTrending { get; set; }
        public bool showSeasonal { get; set; }
        public bool discoverOnlyCurrent { get; set; }
        public bool discoverOnlyCompleted { get; set; }
        public bool discoverOnlyTrending { get; set; }
        public bool discoverOnlySeasonal { get; set; }
    }
}
