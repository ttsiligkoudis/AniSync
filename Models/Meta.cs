
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AnimeList.Models
{
    public partial class Meta
    {
        public Meta(dynamic descriptionRich = null)
        {
            var raw = (string)descriptionRich;
            if (string.IsNullOrEmpty(raw))
            {
                description = string.Empty;
                return;
            }

            // Decode HTML entities first so &lt;i&gt; → <i>, then strip the resulting tags.
            // AniList descriptions in particular sometimes ship with entity-encoded markup
            // that the regex alone can't match.
            var decoded = System.Net.WebUtility.HtmlDecode(raw);
            description = HtmlTagPattern().Replace(decoded, string.Empty);
        }

        public string id { get; set; }
        public string name { get; set; }
        public string poster { get; set; }
        public string description { get; }

        // Surfaced by the per-service catalog queries for the StreamD-style card
        // chrome — the score badge overlay on the poster and the "format · eps ·
        // year" info row under the title. All optional: services that don't have
        // a value (or fail to populate one) leave them null and the partial omits
        // the corresponding chunk gracefully. score is normalised to 0-10 with
        // one decimal so the badge format stays consistent across providers
        // (AniList/Kitsu return 0-100, MAL returns 0-10 already).
        public double? score { get; set; }
        public int? episodes { get; set; }
        public int? year { get; set; }
        public string format { get; set; }

        // Display-ready airing status ("Airing" / "Finished" / "Hiatus" /
        // "Cancelled" / "Not Yet Released") and source-material label
        // ("Manga adaptation" / "Original" / "Novel adaptation" / etc.).
        // Populated on the detail-side per-service Meta builder; null on
        // catalog responses where the data isn't fetched.
        public string airStatus { get; set; }
        public string source { get; set; }

        // User's watched-episode count for THIS entry, populated only when the
        // catalog response naturally includes list-status data (i.e. user-list
        // queries like Currently Watching / Completed; null on Trending /
        // Seasonal / Airing because those return media without per-user
        // context). Cards render an "Ep N / Total" badge when set.
        public int? progress { get; set; }
        public List<string> genres { get; set; }
        public string background { get; set; }
        public string type { get; set; } = MetaType.series.ToString();
        public List<Trailer> trailers { get; set; } = [];
        public List<TrailerStream> trailerStreams { get; set; } = [];
        public List<Video> videos { get; set; } = [];
        public List<Link> links { get; set; } = [];
        public string entryId { get; set; }
        public string entryStatus { get; set; }

        // Slim Meta entries (id + name + poster + score + format + year +
        // episodes) for the "audience also liked" carousel on the detail
        // page. Populated by GetAnimeByIdAsync per service when the
        // upstream provides recommendation data; null/empty on services /
        // entries without recommendations support.
        public List<Meta> recommendations { get; set; } = [];
        // Tells Stremio this series has scheduled / upcoming episodes so it renders the
        // "Upcoming" badge for videos whose `released` date is in the future. defaultVideoId
        // is intentionally null — Stremio uses the videos[] array to decide what to play.
        public MetaBehaviorHints behaviorHints { get; set; } = new();

        [GeneratedRegex("<.*?>")]
        private static partial Regex HtmlTagPattern();
    }

    public class MetaBehaviorHints
    {
        // Always emit as JSON null even though our global STJ options skip nulls — Stremio
        // expects the field to be present, not absent.
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string defaultVideoId { get; set; }
        public bool hasScheduledVideos { get; set; } = true;
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
