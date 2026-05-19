using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeList.Models
{
    public class AnimeIdMapping
    {
        [JsonProperty("mal_id")]
        public int? MalId { get; set; }

        [JsonProperty("anilist_id")]
        public int? AnilistId { get; set; }

        [JsonProperty("kitsu_id")]
        public int? KitsuId { get; set; }

        [JsonProperty("imdb_id")]
        public string ImdbId { get; set; }

        [JsonProperty("themoviedb_id")]
        private JToken TmdbIdRaw { get; set; }

        // Exposed as a plain string for consumers (TmdbService /
        // AnilistFallback / etc. only need the numeric ID).
        // Fribb's JSON shape for this field has gone through three
        // generations: bare int, bare string, and as of mid-2026
        // an object like {"tv": 26209} or {"movie": 12345}. We
        // handle all three so a model update doesn't break with
        // the next format change either. When the value is an
        // object we take the first key's int — TmdbService's
        // FetchImdbFromTmdbAsync tries the /tv endpoint first and
        // falls back to /movie on 404, so it doesn't need us to
        // tell it the type.
        [JsonIgnore]
        public string TmdbId
        {
            get
            {
                if (TmdbIdRaw == null) return null;
                switch (TmdbIdRaw.Type)
                {
                    case JTokenType.Null:
                        return null;
                    case JTokenType.Integer:
                        return TmdbIdRaw.Value<long>().ToString();
                    case JTokenType.String:
                        var s = TmdbIdRaw.Value<string>();
                        return string.IsNullOrEmpty(s) ? null : s;
                    case JTokenType.Object:
                        if (TmdbIdRaw is JObject obj && obj.HasValues)
                        {
                            var first = obj.Properties().FirstOrDefault()?.Value;
                            if (first != null && first.Type != JTokenType.Null)
                                return first.Value<long?>()?.ToString();
                        }
                        return null;
                    default:
                        return null;
                }
            }
        }

        [JsonProperty("thetvdb_id")]
        public int? TvdbId { get; set; }

        [JsonProperty("anidb_id")]
        public int? AnidbId { get; set; }

        [JsonProperty("season")]
        private JObject SeasonRaw { get; set; }

        [JsonIgnore]
        public int? Season
        {
            get
            {
                if (SeasonRaw == null || !SeasonRaw.HasValues)
                    return null; // default

                var firstValue = SeasonRaw.Properties().FirstOrDefault()?.Value;

                if (firstValue == null || firstValue.Type == JTokenType.Null)
                    return null; // default

                return firstValue.Value<int?>();
            }
        }

        [JsonIgnore]
        public string Name { get; set; }

        [JsonIgnore]
        public int? Episodes { get; set; }
    }
}
