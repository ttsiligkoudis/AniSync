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
        public string TmdbId { get; set; }

        [JsonProperty("season")]
        private JObject SeasonRaw { get; set; }

        [JsonIgnore]
        public int? Season
        {
            get
            {
                if (SeasonRaw == null || !SeasonRaw.HasValues)
                    return 1; // default

                var firstValue = SeasonRaw.Properties().FirstOrDefault()?.Value;

                if (firstValue == null || firstValue.Type == JTokenType.Null)
                    return 1; // default

                return firstValue.Value<int?>();
            }
        }
    }
}
