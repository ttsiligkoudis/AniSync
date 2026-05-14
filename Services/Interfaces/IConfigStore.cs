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

        /// <summary>
        /// Single indexed lookup that finds the row owning <paramref name="candidate"/>'s
        /// identity, regardless of whether the candidate is the row's primary provider or
        /// one of its linked secondaries. Returns <c>(uid, isPrimaryMatch)</c> where
        /// <c>isPrimaryMatch == true</c> means the matched slot is the row's primary
        /// (caller should refresh primary tokens) and <c>false</c> means the candidate is
        /// currently a linked secondary on this row (caller should refresh the linked entry
        /// and route the session to the row's existing primary). Returns <c>(null, false)</c>
        /// when no row owns the identity. Backed by the per-service unique partial indexes,
        /// so it's an O(log n) B-tree probe — used by HomeController on every authenticated
        /// page render.
        /// </summary>
        Task<(string uid, bool isPrimaryMatch)> FindUidByIdentityAsync(TokenData candidate);

        /// <summary>Looks up token data by UID. Returns null if the UID is unknown.</summary>
        Task<TokenData> GetAsync(string uid);

        /// <summary>
        /// Updates the token JSON for whichever row matches the user identity carried in
        /// <paramref name="tokenData"/>. Used by token refresh so the install URL stays valid
        /// even when the upstream provider rotates tokens. No-op if no matching row exists.
        /// </summary>
        Task UpdateByUserAsync(TokenData tokenData);

        /// <summary>
        /// Reads the persisted catalog / discover-only / streams toggles for a UID, plus the
        /// row's current revision counter (used by callers building cache-busted install URLs).
        /// Returns (0, 0, 0, 0) if the UID is unknown.
        /// </summary>
        Task<(byte flags1, byte flags2, byte flags3, long revision)> GetFlagsAsync(string uid);

        /// <summary>
        /// Reads only the revision counter for a UID. Returns 0 if the UID is unknown.
        /// </summary>
        Task<long> GetRevisionAsync(string uid);

        /// <summary>
        /// Writes the toggle bytes for the given UID and bumps the revision counter. Returns
        /// the new revision so the caller can rebuild the install URL with cache-busting bytes.
        /// Returns 0 if the UID is unknown.
        /// </summary>
        Task<long> SetFlagsAsync(string uid, byte flags1, byte flags2, byte flags3);

        /// <summary>
        /// Removes the row for <paramref name="uid"/> from the config store. No-op if missing.
        /// Used by the "Delete Configuration" Danger Zone action.
        /// </summary>
        Task DeleteAsync(string uid);

        /// <summary>
        /// Reads the additional (non-primary) provider tokens linked to a UID. Empty list if
        /// the UID is unknown or has no links. Used by the multi-provider sync feature.
        /// </summary>
        Task<List<LinkedToken>> GetLinkedTokensAsync(string uid);

        /// <summary>
        /// Inserts or updates a linked-provider token for a UID, keyed on
        /// <see cref="LinkedToken.Service"/>. Replaces any existing token for the same service.
        /// </summary>
        Task SetLinkedTokenAsync(string uid, LinkedToken linked);

        /// <summary>
        /// Removes the linked token for a (uid, service) pair. No-op if no matching link exists.
        /// </summary>
        Task RemoveLinkedTokenAsync(string uid, AnimeService service);

        /// <summary>
        /// Returns the scrobble token for the given UID, generating + storing one if absent.
        /// Idempotent — repeated calls for the same UID return the same token until
        /// <see cref="RotateScrobbleTokenAsync"/> is called. Returns null if the UID is unknown.
        /// </summary>
        Task<string> EnsureScrobbleTokenAsync(string uid);

        /// <summary>
        /// Generates a fresh scrobble token, replaces any existing one, and returns it. Old
        /// webhook URLs immediately stop working — used by the "Rotate" button on the configure
        /// page.
        /// </summary>
        Task<string> RotateScrobbleTokenAsync(string uid);

        /// <summary>
        /// Reverse lookup: returns the UID associated with a scrobble token, or null when the
        /// token doesn't match any row. The webhook controller uses this to authenticate
        /// inbound requests.
        /// </summary>
        Task<string> ResolveUidByScrobbleTokenAsync(string token);

        /// <summary>
        /// Stores the optional Plex Home username for a UID. When set, Plex events whose
        /// <c>Account.title</c> doesn't match are dropped — handles shared Plex servers where
        /// roommates' viewing should not scrobble onto this user's trackers. Pass null/empty to
        /// clear the filter.
        /// </summary>
        Task SetPlexUsernameAsync(string uid, string username);

        /// <summary>
        /// Reads the configured Plex Home username, or null if no filter is set.
        /// </summary>
        Task<string> GetPlexUsernameAsync(string uid);

        /// <summary>
        /// Stores the user's Real-Debrid API key. Pass null/empty to clear.
        /// Plaintext at rest in v1 — matches the scrobble_token / plex_username
        /// posture; encrypt-at-rest is a known follow-up before public launch.
        /// No-op if the UID is unknown.
        /// </summary>
        Task SetRealDebridApiKeyAsync(string uid, string apiKey);

        /// <summary>
        /// Reads the stored Real-Debrid API key, or null when none is set / the
        /// UID is unknown. Consumed by StreamController and the
        /// /anime/episode-streams endpoint to decide whether to query Torrentio
        /// for RD-cached torrents.
        /// </summary>
        Task<string> GetRealDebridApiKeyAsync(string uid);

        /// <summary>
        /// Swaps the primary provider with the linked token of <paramref name="newPrimaryService"/>.
        /// The chosen link becomes the primary on this row; the previous primary moves into the
        /// linked-tokens array. The UID is preserved so existing install URLs keep working.
        /// Returns the new primary's <see cref="TokenData"/> on success along with a null reason,
        /// or <c>(null, reason)</c> when the swap is rejected. Reasons:
        /// <c>"uid-missing"</c>, <c>"no-primary"</c>, <c>"not-linked"</c>,
        /// <c>"needs-reauth"</c>, <c>"no-token"</c>, <c>"collision"</c>.
        /// When <paramref name="resolveCollision"/> is true, a unique-key collision causes the
        /// stale row to be deleted before retrying instead of returning <c>"collision"</c> —
        /// callers should only set this after asking the user, since it nukes the other row's
        /// flags / linked tokens / install URL.
        /// </summary>
        Task<(TokenData newPrimary, string reason)> SwapPrimaryAsync(string uid, AnimeService newPrimaryService, bool resolveCollision = false);

        /// <summary>
        /// Records a Torrentio info hash as "no longer playable through Real-Debrid"
        /// until <paramref name="expiresAtUtc"/>. UPSERT semantics — if the hash is
        /// already stored with a later expiry, the existing TTL is kept. Persisted
        /// so the list survives process restarts and is shared across app instances;
        /// in-process callers should also keep a short-lived in-memory snapshot
        /// (refreshed every minute or so) to avoid a SQLite hit on every filter call.
        /// </summary>
        Task MarkBadHashAsync(string infoHash, DateTime expiresAtUtc);

        /// <summary>
        /// Returns all lowercase info hashes whose ban is still live relative to
        /// <paramref name="nowUtc"/>. Implementations also purge expired rows
        /// opportunistically on each call, so the table stays bounded even
        /// without a separate cleanup job.
        /// </summary>
        Task<IReadOnlyList<string>> GetActiveBadHashesAsync(DateTime nowUtc);
    }
}
