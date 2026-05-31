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
