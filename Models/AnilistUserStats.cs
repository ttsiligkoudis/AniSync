namespace AnimeList.Models
{
    /// <summary>
    /// Slim projection of AniList's <c>User.statistics.anime</c> GraphQL response —
    /// just the fields the dashboard "Your stats" panel renders. Built by
    /// <see cref="Services.Interfaces.IAnilistService.GetUserStatsAsync"/> so the
    /// dashboard doesn't have to fetch and iterate the full Watching + Completed
    /// lists to compute totals locally.
    /// </summary>
    public record AnilistUserStats(
        int Watching,
        int Completed,
        int TotalHoursWatched,
        double? MeanScore);
}
