using Newtonsoft.Json;

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
    }
}
