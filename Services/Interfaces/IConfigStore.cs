using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Persists per-install token data behind a short opaque UID so the install URL doesn't have
    /// to embed the user's full credentials. Implementations must be safe to use as a singleton.
    /// </summary>
    public interface IConfigStore
    {
        /// <summary>
        /// Inserts a row for <paramref name="tokenData"/> if no row exists for the same
        /// <c>(anime_service, user_key)</c>; otherwise updates the existing row's token JSON.
        /// In either case returns the row's UID — the same UID across re-logins for the same user.
        /// </summary>
        Task<string> UpsertAsync(TokenData tokenData);

        /// <summary>Looks up token data by UID. Returns null if the UID is unknown.</summary>
        Task<TokenData> GetAsync(string uid);

        /// <summary>
        /// Updates the token JSON for whichever row matches the user identity carried in
        /// <paramref name="tokenData"/>. Used by token refresh so the install URL stays valid
        /// even when the upstream provider rotates tokens. No-op if no matching row exists.
        /// </summary>
        Task UpdateByUserAsync(TokenData tokenData);

        /// <summary>
        /// Reads the persisted catalog / discover-only / streams toggles for a UID. Returns
        /// (0, 0, 0) if the UID is unknown — the caller can layer view-side defaults on top.
        /// </summary>
        Task<(byte flags1, byte flags2, byte flags3)> GetFlagsAsync(string uid);

        /// <summary>
        /// Writes the toggle bytes for the given UID. No-op if the UID is unknown.
        /// </summary>
        Task SetFlagsAsync(string uid, byte flags1, byte flags2, byte flags3);
    }
}
