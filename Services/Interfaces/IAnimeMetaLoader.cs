using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Resolves an anime id (anilist:/mal:/kitsu:/tmdb:/tt...) into a
    /// fully-shaped <see cref="Meta"/> plus the surrounding state both the
    /// web app's detail page and the Stremio addon's meta endpoint need.
    /// Captures the shared dispatch + enrichment pipeline so the web app
    /// and the addon produce the same artifact from the same id — the web
    /// app remains the behavioural source of truth and the addon mirrors it.
    ///
    /// Out of scope on purpose: per-user list-entry fetch, sidedata
    /// enrichment (tag / studio / staff / sequel / prequel / similar
    /// links), and surface-specific link decoration (the addon's
    /// /discover URL rewrites + Manage Entry link). Each caller layers
    /// those concerns on top of the result returned here.
    /// </summary>
    public interface IAnimeMetaLoader
    {
        /// <summary>
        /// Loads and enriches a single anime by id. Encodes the full
        /// "what the web app does today" pipeline: imdb-grouped franchise
        /// umbrella synthesis, tmdb dispatch, per-service primary +
        /// cross-service fallback, adult-content gate, cour episode
        /// renumbering, multi-cour-group detection, filler labels, source-
        /// link mapping, AniList airing-schedule overlay.
        /// </summary>
        /// <param name="id">Raw id from the caller's route — kitsu:N / anilist:N / mal:N / tmdb:N / ttNNNNN.</param>
        /// <param name="tokenData">Caller's session; anime_service drives per-service dispatch. May be a synthetic Kitsu default for anonymous viewers.</param>
        /// <param name="groupSeasons">User's "Group anime seasons" pref. Forwarded to every per-service GetAnimeByIdAsync so the streaming-episodes / videos shape matches the catalog the user came from.</param>
        /// <param name="showAdultContent">Caller's per-user adult-content toggle. False (the default) makes the loader gate isAdult entries by returning <c>AdultFiltered=true</c> + <c>Anime=null</c>.</param>
        Task<AnimeMetaLoadResult> LoadAsync(string id, TokenData tokenData, bool groupSeasons, bool showAdultContent);

        /// <summary>
        /// Builds the franchise-umbrella Meta for an imdb-id deep-link
        /// from Cinemeta. Exposed publicly so the Watch action can reuse
        /// the same multi-cour synthesis without going through the full
        /// <see cref="LoadAsync"/> pipeline (Watch already knows it has
        /// a tt id and skips the per-service dispatch / adult gate /
        /// source-link map). Returns null Meta when no Cinemeta data
        /// exists for the id.
        /// </summary>
        Task<(Meta Anime, int? HeadSeason, int? HeadAnilistId)> BuildGroupedImdbAnimeAsync(string imdbId);

        /// <summary>
        /// Translates a cross-service id (imdb tt / mal: in non-MAL
        /// context) into the user's primary's native id. Returns the
        /// input unchanged when no translation is needed or when the
        /// mapping table has no matching row.
        /// </summary>
        Task<string> ResolveToServiceIdAsync(string id, AnimeService service);

        /// <summary>
        /// Overlays AniList's per-episode airing-schedule timestamps onto
        /// a Meta's videos array. Used by both the detail and watch
        /// paths so the click gate's "is this aired yet?" check stays in
        /// sync with the notifier dispatcher. seasonFilter scopes the
        /// overlay to a single cour for grouped-imdb renders so head-
        /// cour times don't bleed onto later seasons' same-numbered
        /// episodes.
        /// </summary>
        Task OverlayAniListAiringScheduleAsync(Meta anime, int? anilistId, int? seasonFilter = null);
    }

    /// <summary>
    /// Output of <see cref="IAnimeMetaLoader.LoadAsync"/>. Carries the
    /// resolved Meta plus the surrounding state callers need to make
    /// further per-surface decisions (entry fetch keyed off the resolved
    /// service id, multi-cour pill vs per-cour pill on the detail page,
    /// head-cour-scoped airing overlay on grouped renders).
    /// </summary>
    public sealed record AnimeMetaLoadResult(
        Meta Anime,
        bool AdultFiltered,
        bool RenderedAsGrouped,
        bool IsMultiSeasonGroup,
        int? ImdbHeadSeason,
        int? ImdbHeadAnilistId,
        AnimeSourceLinks SourceLinks);
}
