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
        /// Returns slim <see cref="Meta"/> entries (id + name + poster +
        /// score + format + year + episodes) for the detail page's
        /// recommendations carousel. Parallels
        /// <see cref="GetRecommendationsAsync"/> but with the richer card-
        /// level data the carousel needs. ids stay in the AniList-prefixed
        /// space (anilist:N); the detail page's controller resolves
        /// cross-service via the mapping when the user clicks a card.
        /// </summary>
        Task<List<Meta>> GetRecommendationMetasAsync(int anilistId);

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

        /// <summary>
        /// Aggregate counts for the current season's anime — used by the
        /// dashboard's "This Season" stat strip. Returns (currentlyAiring,
        /// newThisSeason, totalThisSeason). One AniList GraphQL call with
        /// aliased Page queries; pageInfo.total surfaces each count without
        /// pulling a media result set per query.
        /// </summary>
        Task<(int currentlyAiring, int newThisSeason, int totalThisSeason)> GetSeasonStatsAsync();

        /// <summary>
        /// Anime with an episode airing during the current UTC day (00:00 –
        /// 23:59 UTC). One row per anime — multiple cours of the same show
        /// dropping in the same day collapse to a single card. Slim
        /// <see cref="Meta"/> shape compatible with the dashboard's
        /// scroll-row _PosterGrid. Cached until the next UTC midnight so the
        /// shelf rotates cleanly when the calendar day changes (rather than
        /// after a fixed 24-hour window from first fetch).
        /// </summary>
        Task<List<Meta>> GetNewEpisodesTodayAsync();

        /// <summary>
        /// Prequels and sequels for an AniList anime id, sorted by air year
        /// ascending so the carousel reads chronologically (story-order
        /// approximation — for the rare case where a prequel airs after its
        /// sequel, year-asc still groups them adjacent to the source anime).
        /// Returns slim <see cref="Meta"/> objects compatible with
        /// _PosterGrid scroll variant. Cached 24h per anilist id since
        /// relations are essentially static metadata.
        /// </summary>
        Task<List<Meta>> GetRelatedAsync(int anilistId);

        /// <summary>
        /// AniList-sourced supplementary <see cref="Link"/> entries (Tag,
        /// Studio, director, writer, Composer, Artist, Producer, Staff) for
        /// an anime id. Used by the detail page to augment pages loaded via
        /// non-AniList services (Kitsu / MAL primaries) whose own GetAnimeByIdAsync
        /// doesn't surface this metadata richness. Cached 24h per anilist id —
        /// staff / studio / tag data is essentially immutable.
        /// </summary>
        Task<List<Link>> GetSupplementaryLinksAsync(int anilistId);
    }
}
