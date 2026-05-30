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
    }
}
