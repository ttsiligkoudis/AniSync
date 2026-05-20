using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Resolves IMDb IDs from anime database IDs and vice versa using community-maintained mapping data.
    /// </summary>
    public interface IAnimeMappingService
    {
        Task EnsureLoadedAsync();
        Task<AnimeIdMapping> GetAnilistMapping(string anilistId);
        Task<AnimeIdMapping> GetKitsuMapping(string kitsuId);
        Task<AnimeIdMapping> GetMalMapping(string malId);
        Task<List<AnimeIdMapping>> GetImdbMapping(string imdb, int? season = null);
        Task<List<AnimeIdMapping>> GetTmdbMapping(string tmdbId, int? season = null);
        Task<string> GetIdByService(string animeId, AnimeService service, int? season = null);

        /// <summary>
        /// Convenience over <see cref="GetIdByService"/> that returns the
        /// id with its service prefix already stamped on (e.g.
        /// <c>"anilist:12345"</c>) — saves callers the
        /// service-switch-+-string-interpolation dance they otherwise
        /// repeat at every call site. Returns null when no mapping exists.
        /// </summary>
        Task<string> GetIdWithPrefixAsync(string animeId, AnimeService service, int? season = null);

        /// <summary>
        /// Translates a within-cour episode number into the (season, episode)
        /// coordinates that IMDb-keyed Stremio addons (Torrentio, MediaFusion,
        /// Comet, AIOStreams, …) expect. For most franchises every cour gets
        /// its own IMDb season and the input passes through unchanged; for
        /// franchises whose IMDb listing collapses every cour into one
        /// continuous season (e.g. an anime with "S4 E6" airing as IMDb
        /// "S1 E42"), the helper detects the collapse — multiple cours
        /// share the same <see cref="AnimeIdMapping.Season"/> value — sorts
        /// the cours by service id, computes the cumulative episode offset
        /// from earlier cours via their <see cref="AnimeIdMapping.Episodes"/>
        /// field (enriched on-demand via <paramref name="getSummary"/>),
        /// and returns the absolute coordinates. Mirrors the same cumulative
        /// math <c>CinemetaService.GetCourEpisodesAsync</c> already uses
        /// for slicing per-cour video lists.
        /// </summary>
        /// <param name="animeId">Service-prefixed id of the current cour.</param>
        /// <param name="withinCourEpisode">Episode number inside the cour (1..N).</param>
        /// <param name="service">User's primary tracker — selects which per-cour id field to sort by.</param>
        /// <param name="getSummary">Callback that fetches (title, episode count) for a service-prefixed id. Used to enrich cours whose <c>Episodes</c> field is missing so the cumulative is accurate.</param>
        /// <returns>Translated <c>(Season, Episode)</c>, or null when no IMDb mapping exists for <paramref name="animeId"/>.</returns>
        Task<(int Season, int Episode)?> ResolveImdbStreamCoordinatesAsync(
            string animeId,
            int withinCourEpisode,
            AnimeService service,
            Func<string, Task<(string? Name, int? EpisodeCount)>> getSummary);

        /// <summary>
        /// Walks a list of external IDs (from a webhook payload) in priority order and returns
        /// the first one that resolves to a tracker id for <paramref name="service"/>. Tuples are
        /// <c>(prefix, raw id)</c> where <c>prefix</c> is one of <c>anidbPrefix</c>,
        /// <c>imdbPrefix</c>, <c>tmdbPrefix</c>, or <c>tvdbPrefix</c>. Returns null when no id
        /// resolves — typically because the title isn't anime or the mapping data has a gap.
        /// </summary>
        Task<string> ResolveExternalAsync(IEnumerable<(string prefix, string id)> externalIds, AnimeService service, int? season = null);

        /// <summary>
        /// Returns a sorted list of distinct season numbers available for the given anime ID.
        /// </summary>
        Task<List<int>> GetSeasonsAsync(string animeId);

        Task EnrichImdbMappings(List<AnimeIdMapping> mappings);

        /// <summary>
        /// Resolves every cross-service id we know about for the given anime
        /// into a single <see cref="AnimeSourceLinks"/> bundle — used by the
        /// detail page's source chips and by the RD/Torrentio lookup which
        /// needs the IMDb / Kitsu id to construct a Stremio stream identifier.
        /// Best-effort: any single mapping miss leaves the corresponding
        /// field null instead of failing the whole call.
        /// </summary>
        Task<AnimeSourceLinks> BuildSourceLinksAsync(string animeId);
    }
}
