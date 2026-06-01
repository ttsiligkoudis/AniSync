using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Trakt OAuth + API client. Trakt is a first-class provider
    /// (<see cref="AnimeService.Trakt"/>) — its credentials live in the unified
    /// <see cref="TokenData"/> model (primary or linked), the same as every other
    /// provider. This service owns the Trakt-specific OAuth exchange/refresh and the
    /// Trakt REST API calls.
    /// </summary>
    public interface ITraktService
    {
        /// <summary>True when Trakt:ClientId / ClientSecret are configured.</summary>
        bool IsConfigured { get; }

        /// <summary>The authorize-redirect URL for the OAuth connect flow.</summary>
        string BuildAuthorizeUrl(string state);

        /// <summary>
        /// Exchanges an OAuth authorization code for a Trakt-tagged
        /// <see cref="TokenData"/>, resolving the account username + stable slug
        /// (the identity key) from /users/settings. Returns null on any failure
        /// (bad code, not configured, network).
        /// </summary>
        Task<TokenData> ExchangeCodeAsync(string code);

        /// <summary>
        /// Exchanges a Trakt refresh token for a fresh access/refresh pair, carrying
        /// the prior token's identity (user_id / username) forward. Returns null when
        /// refresh fails.
        /// </summary>
        Task<TokenData> RefreshTokenAsync(TokenData token);

        /// <summary>
        /// Returns a valid (refreshed-if-needed) Trakt token for the UID — resolved from
        /// whichever slot holds it (primary or linked) — persisting a refresh back to that
        /// slot. Returns null when Trakt isn't connected or the token can't be refreshed.
        /// </summary>
        Task<TokenData> GetValidTokenAsync(string uid);

        // ── Reads ───────────────────────────────────────────────────────────

        /// <summary>
        /// The user's Trakt watchlist (movies + shows), newest-added first.
        /// Empty when not connected.
        /// </summary>
        Task<List<TraktListItem>> GetWatchlistAsync(string uid);

        /// <summary>
        /// In-progress playback (continue-watching): movies + episodes the user
        /// started but hasn't finished, most-recently-paused first. Empty when
        /// not connected.
        /// </summary>
        Task<List<TraktListItem>> GetPlaybackAsync(string uid);

        /// <summary>
        /// The user's aggregate Trakt stats (movies / shows / episodes watched +
        /// total watch hours) for the video-mode dashboard's "Your stats" strip.
        /// Returns null when Trakt isn't connected or the call fails, so the
        /// caller hides the panel rather than showing zeros.
        /// </summary>
        Task<TraktUserStats> GetUserStatsAsync(string uid);

        /// <summary>
        /// The user's watched history (movies + episodes), de-duplicated to one
        /// entry per IMDb id. Empty when not connected.
        /// </summary>
        Task<List<TraktListItem>> GetHistoryAsync(string uid);

        /// <summary>
        /// Returns the user's Trakt list for a given AniSync list tab + media type:
        /// Planning → watchlist, Completed → watched history, Current → in-progress
        /// playback. Filtered to the requested <paramref name="mediaType"/>
        /// (movie / series). Other list types return empty (no Trakt analogue).
        /// Drives the media-type-aware Library.
        /// </summary>
        Task<List<TraktListItem>> GetListAsync(string uid, ListType list, MetaType mediaType);

        // ── Writes ──────────────────────────────────────────────────────────

        /// <summary>Adds a movie/show to the watchlist. No-op return false when not connected.</summary>
        Task<bool> AddToWatchlistAsync(string uid, string type, string imdbId);

        /// <summary>Removes a movie/show from the watchlist.</summary>
        Task<bool> RemoveFromWatchlistAsync(string uid, string type, string imdbId);

        /// <summary>
        /// Saves in-progress playback via Trakt's /scrobble/pause so the title
        /// surfaces in the user's Continue Watching (/sync/playback). progress is
        /// a 0-100 percentage; season/episode target a specific series episode
        /// (ignored for movies). Best-effort: returns false (no throw) when Trakt
        /// isn't connected, the id is empty, or progress is out of (0,100).
        /// </summary>
        Task<bool> PauseScrobbleAsync(string uid, string type, string imdbId, int? season, int? episode, double progress);

        /// <summary>
        /// Completes playback via Trakt's /scrobble/stop. At progress ≥ 80 Trakt
        /// scrobbles the item as watched (adds to history) AND clears its
        /// in-progress playback entry — so a finished title leaves Continue
        /// Watching and lands in Watched in one call. Callers marking something
        /// watched should pass 100. season/episode target a series episode
        /// (ignored for movies). Best-effort: false (no throw) on miss.
        /// </summary>
        Task<bool> StopScrobbleAsync(string uid, string type, string imdbId, int? season, int? episode, double progress);

        /// <summary>
        /// Aggregate Trakt state (watchlist / watched / rating) for one movie or
        /// series, for the Manage Entry modal. type is "movie" / "series";
        /// imdbId is the tt-prefixed id. Returns an empty entry (no throw) when
        /// Trakt isn't connected or the id is empty.
        /// </summary>
        /// <summary>
        /// One of Trakt's discovery feeds for the video Discover modes.
        /// type is "movie"/"series"; mode is trending | anticipated | watched |
        /// recommended. genre is a Cinemeta genre name (mapped to a Trakt slug
        /// internally) or null. Public feeds run unauthenticated; "recommended"
        /// needs the user's token (empty list when uid is null). Returns ranked
        /// items (imdb id + title + year) for poster hydration via Cinemeta.
        /// </summary>
        Task<List<TraktListItem>> GetDiscoveryAsync(string uid, string type, string mode, string genre, int page, int limit);

        /// <summary>Rich summary (overview / runtime / certification / trailer / rating / genres) for the detail page. Public.</summary>
        Task<TraktVideoSummary> GetSummaryAsync(string type, string imdbId);

        /// <summary>Cast names + characters (no images in Trakt's API), capped at limit. Public.</summary>
        Task<List<TraktCastMember>> GetCastAsync(string type, string imdbId, int limit);

        /// <summary>Related titles for the "Recommended" row (hydrate to posters via Cinemeta). Public.</summary>
        Task<List<TraktListItem>> GetRelatedAsync(string type, string imdbId, int limit);

        /// <summary>Full episode list for a series (Cinemeta supplies the thumbnails). Public.</summary>
        Task<List<Video>> GetEpisodesAsync(string imdbId);

        Task<TraktVideoEntry> GetVideoEntryAsync(string uid, string type, string imdbId);

        /// <summary>
        /// Applies a Manage Entry save for a movie / series to Trakt:
        ///   status ""        → remove from watchlist;
        ///   "planning"       → add to watchlist;
        ///   "watching"       → remove from watchlist + mark watchedEpisodes watched;
        ///   "completed"      → remove from watchlist + mark watched (movie) / all
        ///                      supplied episodes watched (series).
        /// watchedEpisodes are the (season, episode) coords to add to history
        /// (series only; the caller derives them from the Cinemeta episode list).
        /// inProgress (series + "watching" only) is the episode to seed an
        /// in-progress playback on, so the show surfaces as continue-watching
        /// rather than completed. rating (1-10) is written to /sync/ratings;
        /// null/0 removes any rating. Best-effort: false (no throw) when Trakt
        /// isn't connected.
        /// </summary>
        Task<bool> SaveVideoEntryAsync(string uid, string type, string imdbId, string status,
            IReadOnlyList<(int Season, int Episode)> watchedEpisodes, int? rating,
            (int Season, int Episode)? inProgress = null);

        // ── Unified-fan-out writes (token-based) ────────────────────────────

        /// <summary>
        /// Writes a tracked anime entry to Trakt using the resolved Trakt
        /// <see cref="TokenData"/> (primary or linked): <paramref name="planning"/> adds the
        /// show to the watchlist, otherwise a positive <paramref name="progress"/> adds that
        /// episode to history. Maps the anime id → its IMDb show id internally; returns false
        /// (no throw) when the id can't be mapped. Used by the SyncService fan-out and the
        /// manage-entry / auto-track paths.
        /// </summary>
        Task<bool> SaveEntryAsync(TokenData trakt, string animeId, int? season, int progress, bool planning);

        /// <summary>
        /// Removes a tracked anime entry from Trakt (drops it from the watchlist). Maps the
        /// anime id → its IMDb show id internally; returns false when unmappable.
        /// </summary>
        Task<bool> DeleteEntryAsync(TokenData trakt, string animeId, int? season);
    }
}
