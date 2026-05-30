namespace AnimeList.Models
{
    /// <summary>
    /// A connected Trakt account's OAuth credentials, stored as a dedicated
    /// credential on the user's existing config row (the <c>trakt_token_json</c>
    /// column) — deliberately separate from <see cref="TokenData"/> so the
    /// anime-rooted identity model (anime_service enum, per-service switches)
    /// stays untouched. Trakt is a linked capability on an AniSync account, not
    /// a login identity of its own.
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
