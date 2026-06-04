namespace AnimeList.Models
{
    /// <summary>
    /// Legacy read-only projection of a connected Trakt account's OAuth credentials.
    /// Trakt is now a first-class provider stored in the unified <see cref="TokenData"/>
    /// model (anime_service = Trakt); this shape only survives as the return type of
    /// <see cref="Services.Interfaces.IConfigStore.GetTraktTokenAsync"/>, which the
    /// soon-to-be-removed video section consumes for display. New code should use the
    /// provider's <see cref="TokenData"/> directly.
    /// </summary>
    public class TraktToken
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        // Trakt returns expires_in (seconds) + created_at (unix). We persist the
        // resolved absolute expiry so the refresh check is a single comparison
        // and survives across the storage round-trip.
        public DateTime? expiration_date { get; set; }
        // Resolved from /users/settings at connect time so the UI can show
        // "Connected as {username}" without an extra call on every render.
        public string username { get; set; }

        // Connected + usable. A row whose token failed to refresh (revoked /
        // expired beyond refresh) clears the column entirely, so a non-null
        // token with an access_token is the connected signal.
        public bool Connected => !string.IsNullOrEmpty(access_token);
    }
}
