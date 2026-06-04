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
        // Artwork from Trakt (extended=images; https-prefixed). null when Trakt
        // has none — the detail page keeps the Cinemeta image as the fallback.
        public string Poster { get; set; }
        public string Background { get; set; }
        public List<string> Genres { get; set; } = new();
    }

    /// <summary>A cast member from Trakt's /people endpoint. Headshot needs extended=images.</summary>
    public class TraktCastMember
    {
        public string Name { get; set; }
        public string Character { get; set; }
        // Trakt headshot URL (from images.headshot[0], https-prefixed); null when
        // the person has no image on Trakt.
        public string Image { get; set; }
        // Trakt person slug (e.g. "karl-urban") — links the cast card to the
        // actor's filmography at /discover/actor/{slug}. null when absent.
        public string Slug { get; set; }
    }
}
