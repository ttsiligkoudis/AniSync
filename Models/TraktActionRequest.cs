namespace AnimeList.Models
{
    /// <summary>
    /// Body for the cookie-session Trakt write endpoints (history / watchlist /
    /// scrobble). The video section keys everything off the IMDb id + type, with
    /// optional season/episode for series and progress for scrobble.
    /// </summary>
    public class TraktActionRequest
    {
        public string type { get; set; }     // "movie" | "series"
        public string id { get; set; }       // IMDb tt id
        public int? season { get; set; }
        public int? episode { get; set; }
        public double? progress { get; set; }
        public string action { get; set; }   // watchlist: add|remove · scrobble: start|pause|stop
    }
}
