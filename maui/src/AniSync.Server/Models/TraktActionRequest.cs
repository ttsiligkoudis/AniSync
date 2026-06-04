namespace AnimeList.Models
{
    /// <summary>
    /// Body for the cookie-session Trakt watchlist toggle (/anime/trakt-watchlist).
    /// Keys off the IMDb id + type; action is add|remove. (History now rides the
    /// unified /anime/mark-watched auto-track; live scrobble was retired.)
    /// </summary>
    public class TraktActionRequest
    {
        public string type { get; set; }     // "movie" | "series"
        public string id { get; set; }       // IMDb tt id
        public int? season { get; set; }
        public int? episode { get; set; }
        public string action { get; set; }   // watchlist: add|remove
    }
}
