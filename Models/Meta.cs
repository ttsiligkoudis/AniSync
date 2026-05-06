
using System.Text.RegularExpressions;

namespace AnimeList.Models
{
    public partial class Meta
    {
        public Meta(dynamic descriptionRich = null)
        {
            description = string.IsNullOrEmpty((string)descriptionRich) ? string.Empty : HtmlTagPattern().Replace((string)descriptionRich, string.Empty);
        }

        public string id { get; set; }
        public string name { get; set; }
        public string poster { get; set; }
        public string description { get; }
        public List<string> genres { get; set; }
        public string background { get; set; }
        public string type { get; set; } = MetaType.series.ToString();
        public List<Trailer> trailers { get; set; } = [];
        public List<TrailerStream> trailerStreams { get; set; } = [];
        public List<Video> videos { get; set; } = [];
        public List<Link> links { get; set; } = [];
        public string entryId { get; set; }
        public string entryStatus { get; set; }

        [GeneratedRegex("<.*?>")]
        private static partial Regex HtmlTagPattern();
    }

    public class Trailer(dynamic id)
    {
        public string source { get; set; } = id;
        public string type => "Trailer";
    }

    public class TrailerStream(dynamic id, dynamic title)
    {
        public string title { get; set; } = title;
        public string ytId { get; set; } = id;
    }

    public class Video
    {
        public string id { get; set; }
        public string title { get; set; }
        public string thumbnail { get; set; }
        public int season { get; set; }
        public int episode { get; set; }
        // ISO-8601 air date — Stremio renders this in the episode card.
        public string released { get; set; }
        // Per-episode synopsis. Populated from Cinemeta when a cross-service IMDb mapping
        // exists; otherwise null and Stremio falls back to the show synopsis.
        public string overview { get; set; }
        public string description { get; set; }
        public string name { get; set; }
        public string firstAired{ get; set; }
    }

    public class Edge
    {
        public string relationType { get; set; }
        public dynamic node { get; set; }
    }

    public class Link
    {
        public string name { get; set; }
        public string category { get; set; }
        public string url { get; set; }
    }

    /// <summary>
    /// A "watch on platform X" link, surfaced from AniList <c>externalLinks</c> or Kitsu
    /// <c>streamingLinks</c>. Currently used by <see cref="Controllers.StreamController"/>.
    /// </summary>
    public class StreamingLink
    {
        public string Site { get; set; }
        public string Url { get; set; }
    }
}
