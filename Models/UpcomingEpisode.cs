namespace AnimeList.Models
{
    /// <summary>
    /// Slim airing-schedule entry returned by
    /// <c>IAnilistFallback.GetUpcomingEpisodesAsync</c>. Keeps the AniList id
    /// raw (no prefix) so the dispatcher can hand it to the mapping service
    /// directly.
    /// </summary>
    public record UpcomingEpisode(
        int AnilistId,
        string Title,
        int Episode,
        long AiringAt,
        string CoverImage);
}
