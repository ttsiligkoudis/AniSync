
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

        // Average episode runtime in minutes (AniList's `duration` field).
        // Surfaced in the detail-page info row. null when the upstream
        // didn't ship a value or the entry is a movie (where the format
        // string already implies "single runtime").
        public int? avgDuration { get; set; }

        // Tags (themes like "Reincarnation", "Cyberpunk", "Coming-of-age")
        // beyond the broad genres list. Top-ranked subset only — the full
        // AniList tag list runs 30+ entries per anime. Detail page renders
        // a small subdued chip strip beneath genres.
        public List<string> tags { get; set; } = [];

        // User's watched-episode count for THIS entry, populated only when the
        // catalog response naturally includes list-status data (i.e. user-list
        // queries like Currently Watching / Completed; null on Trending /
        // Seasonal / Airing because those return media without per-user
        // context). Cards render an "Ep N / Total" badge when set.
        public int? progress { get; set; }

        // Episode number that's airing in the current shelf window — populated
        // by GetNewEpisodesTodayAsync from AniList's airingSchedule and
        // surfaced as an "Ep N" top-left badge on the "New Episodes Today"
        // cards. Distinct from <see cref="progress"/> (user-list state); the
        // two never coexist on the same card today, but both feed the same
        // top-left badge slot in _PosterGrid.
        public int? airingEpisode { get; set; }

        // Unix-seconds airing timestamp paired with airingEpisode. Surfaced
        // as a "· HH:mm" suffix on the badge, formatted client-side to the
        // viewer's local timezone since the shelf is served to users in
        // different timezones from a single cached build.
        public long? airingAt { get; set; }
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

    public class TrailerStream
    {
        public string title { get; set; }
        public string ytId { get; set; }

        public TrailerStream(dynamic id, dynamic title)
        {
            this.title = title;
            this.ytId = ExtractYouTubeId((string)id);
        }

        // AniList sometimes hands us trailer.id with stray query parameters
        // appended ("ZrQoGBYHzIU&source_ve_path=MTc4NDI0" instead of the bare
        // 11-char video id). Plugging that into youtube.com/embed/{id} yields a
        // malformed embed URL that YouTube rejects with "error 153 — video
        // player configuration error". Other providers (MAL / Kitsu / TMDB)
        // generally hand us a clean id, but normalising here means every
        // upstream gets the same defence and we don't have to chase each
        // service's quirks individually.
        //
        // Order of attempts:
        //   1. Fast path — already an exact 11-char id-shaped string. No regex.
        //   2. Anchored match: an 11-char id preceded by a YouTube-URL
        //      separator (=, /, ?, &) and not abutting another id char on
        //      either side. Handles ?v=, /embed/, /shorts/, youtu.be/, etc.
        //   3. Greedy fallback: first 11-char id-char run anywhere in the
        //      string. Catches the AniList "id with junk after" shape.
        //   4. Give up and return the original — keeps the legacy fallback
        //      semantics so a future format we haven't anticipated still
        //      produces *some* href to YouTube even if the embed fails.
        private static string ExtractYouTubeId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            if (raw.Length == 11 && raw.All(IsYouTubeIdChar))
                return raw;

            var anchored = System.Text.RegularExpressions.Regex.Match(
                raw, @"(?:^|[?&/=])([A-Za-z0-9_-]{11})(?:[^A-Za-z0-9_-]|$)");
            if (anchored.Success) return anchored.Groups[1].Value;

            var loose = System.Text.RegularExpressions.Regex.Match(
                raw, @"[A-Za-z0-9_-]{11}");
            return loose.Success ? loose.Value : raw;
        }

        private static bool IsYouTubeIdChar(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-';
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

        // Original IMDb (Cinemeta) coordinates, preserved when the
        // AnimeController-side NormaliseCourEpisodeNumbering collapses
        // the cour to within-cour 1..N for display + routing. The stream
        // lookup needs the IMDb-absolute values to query addons against
        // the right episode of the right IMDb season (e.g. a Naruto-style
        // 220-episode show split across 5 IMDb seasons would otherwise
        // collapse to "S1 E100" which addons can't resolve). Null when
        // the video didn't come from Cinemeta — falls back to using
        // <see cref="season"/> / <see cref="episode"/> at the call site.
        public int? imdbSeason { get; set; }
        public int? imdbEpisode { get; set; }
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

        // AniList numeric id for tag / staff / studio links. Populated by the
        // AniList per-service builder so the detail page can wire chip clicks
        // to internal routes (/discover/staff/{id}, /discover/studio/{id}, /discover/tag/{name})
        // instead of always bouncing to AniList. Null on services that
        // don't expose ids (Kitsu/MAL minimal metadata), in which case the
        // chip falls back to the external url.
        public long? anilistId { get; set; }
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
