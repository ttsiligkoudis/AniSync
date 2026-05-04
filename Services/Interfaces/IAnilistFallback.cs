using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Anonymous-only AniList queries used as cross-service fallbacks when the user's chosen
    /// service can't natively satisfy a feature (e.g. Kitsu users get AniList's airing schedule
    /// here because Kitsu has no equivalent endpoint). Standalone — does not depend on the
    /// authenticated <see cref="IAnilistService"/>, so wiring this up doesn't introduce a
    /// circular dependency between AnilistService and KitsuService.
    /// </summary>
    public interface IAnilistFallback
    {
        /// <summary>
        /// Fetches the next set of airing episodes (sorted by air time, ascending). Each result's
        /// id is rewritten to the most stable form available for the requested service:
        /// IMDb &gt; TMDB &gt; service-native &gt; AniList fallback.
        /// </summary>
        Task<List<Meta>> GetAiringScheduleAsync(AnimeService translateTo, string skip = null);

        /// <summary>
        /// Fetches up to 25 recommendations for an AniList anime id. Each recommendation is
        /// returned as a <see cref="Link"/> with category "Similar" and a url pointing at the
        /// AniList page (clients can follow / share). Ids are translated to the requested
        /// service for use in cross-service rendering.
        /// </summary>
        Task<List<Link>> GetRecommendationsAsync(int anilistId, AnimeService translateTo);

        /// <summary>
        /// Fetches the legal-streaming destinations (Crunchyroll, Netflix, …) for an AniList
        /// anime id. Used by services that don't expose streaming links natively (currently
        /// MyAnimeList) so MAL users still see the same external-service streams in Stremio.
        /// </summary>
        Task<List<StreamingLink>> GetExternalLinksAsync(int anilistId);

        /// <summary>
        /// Fetches the YouTube trailer id for an AniList anime id, or null if the anime has
        /// no trailer or it's not on YouTube. Used by services that don't expose trailers
        /// reliably (currently MyAnimeList).
        /// </summary>
        Task<string> GetYoutubeTrailerIdAsync(int anilistId);
    }
}
