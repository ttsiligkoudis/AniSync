using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Trakt OAuth + API client for the video section. Trakt is a linked
    /// capability on an AniSync account (stored via
    /// <see cref="IConfigStore.SetTraktTokenAsync"/>), not a login identity.
    /// </summary>
    public interface ITraktService
    {
        /// <summary>True when Trakt:ClientId / ClientSecret are configured.</summary>
        bool IsConfigured { get; }

        /// <summary>The authorize-redirect URL for the OAuth connect flow.</summary>
        string BuildAuthorizeUrl(string state);

        /// <summary>
        /// Exchanges an OAuth authorization code for a <see cref="TraktToken"/>,
        /// resolving the account username from /users/settings. Returns null on
        /// any failure (bad code, not configured, network).
        /// </summary>
        Task<TraktToken> ExchangeCodeAsync(string code);

        /// <summary>
        /// Returns a valid (refreshed-if-needed) token for the UID, persisting a
        /// refresh back to the store. Returns null when Trakt isn't connected or
        /// the token can't be refreshed (in which case the connection is cleared).
        /// </summary>
        Task<TraktToken> GetValidTokenAsync(string uid);

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
