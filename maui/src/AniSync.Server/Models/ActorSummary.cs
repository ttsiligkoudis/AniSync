namespace AnimeList.Models
{
    /// <summary>
    /// A single actor tile for the /discover/actors directory (TMDB popular
    /// people). Links through to the filmography at /discover/actor/tmdb/{id},
    /// which bridges to Trakt for the imdb-keyed movie / series cards.
    /// </summary>
    public class ActorSummary
    {
        public int TmdbId { get; set; }
        public string Name { get; set; }
        // TMDB profile image (full url); null when the person has no photo.
        public string Image { get; set; }
    }
}
