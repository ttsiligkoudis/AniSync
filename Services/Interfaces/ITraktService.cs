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

        // ── Writes ──────────────────────────────────────────────────────────

        /// <summary>Adds a movie/show to the watchlist. No-op return false when not connected.</summary>
        Task<bool> AddToWatchlistAsync(string uid, string type, string imdbId);

        /// <summary>Removes a movie/show from the watchlist.</summary>
        Task<bool> RemoveFromWatchlistAsync(string uid, string type, string imdbId);

        /// <summary>
        /// Marks a movie (or a specific series episode when season+episode are
        /// supplied) as watched — adds it to the Trakt history.
        /// </summary>
        Task<bool> AddToHistoryAsync(string uid, string type, string imdbId, int? season, int? episode);

        /// <summary>
        /// Sends a scrobble action ("start" | "pause" | "stop") with playback
        /// progress (0–100). Trakt auto-marks watched on a "stop" past ~80%.
        /// </summary>
        Task<bool> ScrobbleAsync(string uid, string action, string type, string imdbId, int? season, int? episode, double progress);
    }
}
