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
        /// Fetches the next set of airing episodes (sorted by air time, ascending). Each
        /// result's id is rewritten per the caller's <paramref name="groupSeasons"/> pref:
        /// when true (the default), the most stable franchise form — IMDb &gt; TMDB &gt;
        /// service-native &gt; AniList fallback; when false, the user's primary's per-cour
        /// native id (kitsu:/mal:/anilist:) so distinct cours stay distinct cards instead
        /// of collapsing onto one IMDb umbrella.
        ///
        /// When <paramref name="genre"/> is non-empty, the upcoming-episode schedule query
        /// (which has no genre dimension on AniList) is swapped for a "currently airing
        /// anime in that genre" query — same semantic intent ("show me what's airing"),
        /// just sourced from <c>Media(status: RELEASING, genre: $genre)</c> instead of
        /// <c>airingSchedules</c>.
        /// </summary>
        Task<List<Meta>> GetAiringScheduleAsync(AnimeService translateTo, string skip = null, string genre = null, bool hideAdult = false, bool groupSeasons = true);

        /// <summary>
        /// Fetches up to 25 recommendations for an AniList anime id. Each recommendation is
        /// returned as a <see cref="Link"/> with category "Similar" and a
        /// web.stremio.com deep-link URL whose id is resolved per-caller
        /// (translateTo + groupSeasons) — same per-user id-space mapping the
        /// Sequel / Prequel chips use so a chip tap stays inside whichever
        /// addon catalog the user's Stremio is configured for instead of
        /// always pointing at anilist:N.
        /// </summary>
        Task<List<Link>> GetRecommendationsAsync(int anilistId, AnimeService translateTo, bool groupSeasons);

        /// <summary>
        /// Returns slim <see cref="Meta"/> entries (id + name + poster +
        /// score + format + year + episodes) for the detail page's
        /// recommendations carousel. Parallels
        /// <see cref="GetRecommendationsAsync"/> but with the richer card-
        /// level data the carousel needs. ids stay in the AniList-prefixed
        /// space (anilist:N); the detail page's controller resolves
        /// cross-service via the mapping when the user clicks a card.
        /// </summary>
        Task<List<Meta>> GetRecommendationMetasAsync(int anilistId, AnimeService translateTo = AnimeService.Anilist, bool groupSeasons = false);

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
        /// <summary>
        /// Anime with at least one episode airing today, where "today" is
        /// the calendar day in the viewer's timezone. <paramref name="tzOffsetMinutes"/>
        /// follows the JS <c>Date.getTimezoneOffset()</c> convention —
        /// minutes west of UTC, so UTC+3 sends -180 and UTC-5 sends 300.
        /// Defaults to 0 (UTC) when the cookie that sources the offset
        /// hasn't been set yet (e.g. a brand-new visitor's first request);
        /// the layout's inline script seeds it for every subsequent
        /// render. One row per anime (multi-cour drops collapse into a
        /// single card). Slim <see cref="Meta"/> shape compatible with the
        /// dashboard's scroll-row _PosterGrid. Cached for an hour per
        /// (date, tz) bucket so the shelf rotates predictably without
        /// per-render upstream calls.
        /// </summary>
        Task<List<Meta>> GetNewEpisodesTodayAsync(
            AnimeService translateTo = AnimeService.Anilist,
            int tzOffsetMinutes = 0,
            bool groupSeasons = false);

        /// <summary>
        /// Episodes airing within an arbitrary [startUnix, endUnix] window
        /// (Unix seconds, UTC). Used by the per-user notification dispatcher,
        /// which runs every 5 minutes and needs a sliding "now-1h to now+24h"
        /// window rather than the calendar-day shape
        /// <see cref="GetNewEpisodesTodayAsync"/> returns. Not cached — the
        /// caller's cron is the cadence. Stays in the anilist-prefixed id
        /// space; translation to MAL/Kitsu happens downstream via the mapping
        /// service.
        /// </summary>
        Task<List<UpcomingEpisode>> GetUpcomingEpisodesAsync(long startUnix, long endUnix);

        /// <summary>
        /// Per-episode airing entries for a SET of AniList anime ids, filtered to
        /// the [<paramref name="startUnix"/>, <paramref name="endUnix"/>] window
        /// (Unix seconds, UTC). Unlike <see cref="GetUpcomingEpisodesAsync"/> — which
        /// pulls the global airing feed and is bounded to ~48h — this queries each
        /// media's own <c>airingSchedule</c> in id-batches, so the cost scales with
        /// the user's list size rather than the whole airing world. Used by the
        /// month-grid Calendar page, which needs both recently-aired and upcoming
        /// episodes across an arbitrary month for just the shows a user tracks.
        /// Stays in the anilist-prefixed id space; the caller maps back to the
        /// user's primary service for deep-links.
        /// </summary>
        Task<List<UpcomingEpisode>> GetAiringForMediaAsync(IReadOnlyCollection<int> anilistIds, long startUnix, long endUnix);

        /// <summary>
        /// Per-episode airing timestamps for an AniList anime id. AniList's
        /// <c>airingSchedule</c> is community-maintained and tracks the actual
        /// broadcast date faster than Cinemeta's <c>released</c> field, which
        /// occasionally lags 1–2 days behind a real-world release. Returns a
        /// dictionary keyed by episode number with Unix-seconds timestamps so
        /// the caller can overlay airing dates onto a video list matched by
        /// episode ordinal. Cached for 1h per anilist id to keep the detail-
        /// page render fast on repeat hits without staleness ever exceeding
        /// the cron-driven notification cadence. Empty when AniList has no
        /// schedule for the anime (older finished shows often don't).
        /// </summary>
        Task<Dictionary<int, long>> GetAiringScheduleByAnilistIdAsync(int anilistId);

        /// <summary>
        /// Prequels and sequels for an AniList anime id, sorted by air year
        /// ascending so the carousel reads chronologically (story-order
        /// approximation — for the rare case where a prequel airs after its
        /// sequel, year-asc still groups them adjacent to the source anime).
        /// Returns slim <see cref="Meta"/> objects compatible with
        /// _PosterGrid scroll variant. Cached 24h per anilist id since
        /// relations are essentially static metadata.
        /// </summary>
        Task<List<Meta>> GetRelatedAsync(int anilistId, AnimeService translateTo = AnimeService.Anilist, bool groupSeasons = false);

        /// <summary>
        /// Stremio-shape relation links — Sequel / Prequel labels (the only
        /// two categories Stremio's meta UI renders as relation chips) with
        /// a web.stremio.com deep-link URL per entry. Same underlying
        /// GraphQL data as <see cref="GetRelatedAsync"/>, but preserves the
        /// relationType labels that the Meta-returning variant collapses.
        /// Backs MetaController's imdb-grouped enrichment, which has to
        /// inject these into the raw Cinemeta JSON because the imdb path
        /// never goes through AnilistService's inline relation builder.
        /// The id in each Link's url is resolved per-caller (translateTo +
        /// groupSeasons) so a chip tap lands on whichever catalog the
        /// user's Stremio is configured for instead of always anilist:N.
        /// </summary>
        Task<List<Link>> GetRelatedLinksAsync(int anilistId, AnimeService translateTo, bool groupSeasons);

        /// <summary>
        /// Resolves an AniList id into the Stremio meta id the user's
        /// catalog uses — tt... / tmdb:... when groupSeasons is on and a
        /// cross-service mapping exists, otherwise the user's primary's
        /// native id (kitsu:N / mal:N / anilist:N). Used to stamp the
        /// "Similar" + Sequel / Prequel Stremio chip URLs so taps stay
        /// inside the user's installed addon catalog instead of always
        /// resolving to anilist:N. Wraps the same external/native pair
        /// the meta-translation path uses internally.
        /// </summary>
        Task<string> ResolveStremioIdAsync(int anilistId, AnimeService translateTo, bool groupSeasons);

        /// <summary>
        /// Last-resort episode list for entries with no IMDb mapping —
        /// pulls AniList's per-episode <c>streamingEpisodes</c> data
        /// (Crunchyroll / Funimation thumbnails + titles when AniList
        /// has it) and falls back to a synthetic <c>Episode N</c> list
        /// keyed off AniList's <c>episodes</c> count when
        /// streamingEpisodes is empty. Wires the same id-to-AniList
        /// conversion every primary already does via the mapping
        /// table, so Kitsu / MAL primaries (whose native episode
        /// endpoints can be sparse for newer or niche anime) still
        /// get a populated episode list instead of a blank detail page.
        /// Videos come back stamped with season=1; callers should
        /// apply NormalizeVideoIds + any franchise-side season
        /// restoration themselves.
        /// </summary>
        Task<List<Video>> GetEpisodeVideosAsync(int anilistId);

        /// <summary>
        /// Walks a list of Meta and replaces every anilist:N id with the
        /// equivalent kitsu:N / mal:N native id when the mapping table has
        /// one for the requested service. Anime without a matching mapping
        /// keep their anilist:N id (the detail-page route handles it; the
        /// only thing the user loses is in-app Manage Entry on those rows).
        /// Used to translate results from <see cref="IAnilistService"/>
        /// calls that don't natively know the user's primary service
        /// (HomeController's popular-by-season shelves).
        ///
        /// When <paramref name="groupSeasons"/> is true the per-user pref
        /// flips the resolver to IMDb-first (with TMDB / service-native
        /// fallback), matching what the Stremio addon catalog returns when
        /// the same toggle is on — so cards render with imdb:tt... ids and
        /// click-throughs land on the franchise umbrella page regardless of
        /// which service is the user's primary.
        /// </summary>
        Task<List<Meta>> TranslateMetaIdsAsync(List<Meta> metas, AnimeService translateTo, bool groupSeasons = false);

        /// <summary>
        /// Per-user id rewrite for metas that already carry a service-native
        /// id (per-service GetAnimeByIdAsync recommendations,
        /// MetaController search results, etc. — anything that didn't pass
        /// through <see cref="TranslateMetaIdsAsync"/>). When
        /// <paramref name="groupSeasons"/> is true each mappable id is
        /// rewritten to its imdb tt-id; entries the mapping table can't
        /// resolve stay unchanged (the card still links somewhere
        /// reasonable). No-op when groupSeasons is false.
        /// </summary>
        Task<List<Meta>> ApplyGroupingToMetasAsync(List<Meta> metas, bool groupSeasons);

        /// <summary>
        /// AniList-sourced supplementary <see cref="Link"/> entries (Tag,
        /// Studio, director, writer, Composer, Artist, Producer, Staff) for
        /// an anime id. Used by the detail page to augment pages loaded via
        /// non-AniList services (Kitsu / MAL primaries) whose own GetAnimeByIdAsync
        /// doesn't surface this metadata richness. Cached 24h per anilist id —
        /// staff / studio / tag data is essentially immutable.
        /// </summary>
        Task<List<Link>> GetSupplementaryLinksAsync(int anilistId);

        /// <summary>
        /// AniList's full-bleed banner image URL (~1900×800) for the
        /// detail / watch page hero. MAL's pictures[0].large tops out
        /// around 600-1000px and scales poorly on big screens / TVs,
        /// so MAL primaries fall back to this banner when the per-
        /// service path didn't surface a high-resolution image.
        /// Returns null when AniList has no banner for the anime
        /// (rare — usually only brand-new entries or non-anime
        /// classifications). Reads from the same cached Sidedata
        /// bundle the chip-strip endpoints use, so this carries no
        /// extra GraphQL round-trip beyond the existing per-anime
        /// fetch.
        /// </summary>
        Task<string> GetBannerImageAsync(int anilistId);

        /// <summary>
        /// Anonymous browse of every anime tagged with <paramref name="tag"/>,
        /// sorted by popularity desc. Skip / pagination uses the same offset
        /// convention discover-pagination follows (skip = number of cards
        /// already rendered). Translates each result's id into the requested
        /// service's id space so card clicks land on the user's primary's
        /// detail page rather than AniList's.
        /// </summary>
        Task<List<Meta>> GetByTagAsync(string tag, AnimeService translateTo, string skip = null, bool hideAdult = false, bool groupSeasons = false);

        /// <summary>
        /// Page-based variant of <see cref="GetByTagAsync"/> backing the
        /// dedicated /discover/tag/{tagStr} drill-down. Returns AniList's
        /// hasNextPage alongside the page's media so the infinite-scroll
        /// handler can stop at the real end of the catalog. Page is
        /// 1-indexed (AniList's convention).
        /// </summary>
        Task<(List<Meta> Items, bool HasNextPage)> GetByTagPageAsync(string tag, AnimeService translateTo, int page = 1, bool hideAdult = false, bool groupSeasons = false);

        /// <summary>
        /// Full tag catalog from AniList's MediaTagCollection — surfaced by
        /// the /discover/tag listing page. Returns the entire tag list in
        /// one upstream call (AniList doesn't paginate this endpoint),
        /// filtered to non-adult entries and ordered so categories cluster
        /// together for the view's grouped rendering. Cached 24h since
        /// AniList's tag taxonomy shifts on the order of months.
        /// </summary>
        Task<List<TagSummary>> GetTagsListAsync();

        /// <summary>
        /// Browse a staff member's filmography — every anime they're
        /// credited on, sorted by popularity desc. Returns the staff's
        /// display name alongside the media list so the page header can
        /// render "Anime by Hayao Miyazaki" without an extra round-trip.
        /// Name is null when the staff id doesn't resolve.
        /// </summary>
        Task<(string Name, List<Meta> Items)> GetStaffMediaAsync(int staffId, AnimeService translateTo, string skip = null, bool hideAdult = false, bool groupSeasons = false);

        /// <summary>
        /// Browse a studio's catalog — every anime the studio produced,
        /// sorted alphabetically (TITLE_ROMAJI). Returns the studio's
        /// name with the media list for the same reason
        /// GetStaffMediaAsync does, plus AniList's hasNextPage so the
        /// infinite-scroll handler can stop at the real end of the
        /// catalog (Studio.media is filtered client-side to drop manga
        /// edges, so an empty page can still precede more anime pages —
        /// callers must consult HasNextPage, not list emptiness).
        /// Page is 1-indexed.
        /// </summary>
        Task<(string Name, List<Meta> Items, bool HasNextPage)> GetStudioMediaAsync(int studioId, AnimeService translateTo, int page = 1, bool hideAdult = false, bool groupSeasons = false);

        /// <summary>
        /// One page of studios from AniList's Page.studios connection,
        /// sorted by popularity (FAVOURITES_DESC). Each entry carries its
        /// <see cref="StudioSummary.AnimeCount"/> from the inline
        /// <c>media { pageInfo { total } }</c> sub-query so the tile can
        /// show "· N anime" without a per-studio follow-up. The list is
        /// pre-filtered to <c>isAnimationStudio=true</c> with at least
        /// one anime, so the returned count can be smaller than perPage
        /// (sometimes zero) — callers must use <c>HasNextPage</c>, not
        /// list emptiness, to decide whether more pages remain.
        ///
        /// Page is 1-indexed (matches AniList's convention). The /studio
        /// listing renders page 1 server-side and the JS paginator fetches
        /// subsequent pages on scroll. Cached 24h per page key so re-scrolls
        /// don't replay the upstream call.
        /// </summary>
        Task<(List<StudioSummary> Studios, bool HasNextPage)> GetStudiosListAsync(int page = 1, string search = null);
    }
}
