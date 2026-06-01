using System.Collections.Generic;

namespace AnimeList.Models
{
    /// <summary>
    /// Rich movie/series details from Trakt's summary endpoint (extended=full),
    /// used to enrich the Cinemeta-backed video detail page (Trakt has the text;
    /// Cinemeta keeps the images + episodes).
    /// </summary>
    public class TraktVideoSummary
    {
        public string Title { get; set; }
        public int? Year { get; set; }
        public string Overview { get; set; }
        // Movie: total runtime; series: average episode runtime — both in minutes.
        public int? Runtime { get; set; }
        public string Certification { get; set; }
        public double? Rating { get; set; }
        // Trailer URL (usually YouTube); null when none.
        public string Trailer { get; set; }
        public List<string> Genres { get; set; } = new();
    }

    /// <summary>A cast member from Trakt's /people endpoint (no images in the API).</summary>
    public class TraktCastMember
    {
        public string Name { get; set; }
        public string Character { get; set; }
    }
}
