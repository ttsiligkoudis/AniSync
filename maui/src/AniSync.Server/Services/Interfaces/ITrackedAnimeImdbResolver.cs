namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Resolves the set of IMDb ids a user already tracks as anime (via their AniList / MAL / Kitsu
    /// watching list). Both the Calendar and the series-episode notifications draw "series" entries from
    /// Trakt, and a Trakt watchlist frequently contains the same shows the user also tracks on the anime
    /// side — which surfaced the same title twice (e.g. Re:ZERO as the Trakt umbrella "S1E76" AND as the
    /// AniList cour "Season 4 · E10"). Excluding any Trakt imdb id that maps to a tracked anime collapses
    /// that to the single anime entry, matching the rule "if I have it as anime, don't show the Trakt copy".
    /// <para>
    /// Only meaningful when the user's primary tracker is a real anime service; a Trakt-primary user has no
    /// separate anime list to dedupe against, so the resolver returns an empty set and nothing is excluded.
    /// </para>
    /// </summary>
    public interface ITrackedAnimeImdbResolver
    {
        Task<HashSet<string>> GetTrackedAnimeImdbIdsAsync(string uid, CancellationToken ct = default);
    }
}
