
using System.Text.RegularExpressions;

namespace AnimeList.Models
{
    public partial class Meta
    {
        private static readonly Regex HtmlTagRegex = HtmlTagPattern();

        public string id { get; set; }
        public int? malId { get; set; }
        public string imdbId { get; set; }
        public string name { get; set; }
        public string poster { get; set; }
        public string descriptionRich { get; set; }
        public string description => string.IsNullOrEmpty(descriptionRich) ? string.Empty : HtmlTagRegex.Replace(descriptionRich, string.Empty);
        public List<string> genres { get; set; }
        public string background { get; set; }
        public string type { get; set; } = MetaType.series.ToString();
        public List<Trailer> trailers { get; set; } = [];
        public List<TrailerStream> trailerStreams { get; set; } = [];
        public List<Video> videos { get; set; } = [];
        public List<Link> links { get; set; } = [];
        public string entryId { get; set; }

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
        public string season { get; set; }
        public string episode { get; set; }
    }

    public class Link
    {
        public string name { get; set; }
        public string category { get; set; }
        public string url { get; set; }
    }
}
