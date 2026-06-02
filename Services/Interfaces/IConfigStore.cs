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
        /// <paramref name="showExternalStreamsOnCreate"/> seeds the "External services" toggle on
        /// for a brand-new row only — the /account entry point turns it on, /configure leaves it
        /// off. It has no effect when an existing row is refreshed (that path keeps its flags).
        /// </summary>
        Task<string> UpsertAsync(TokenData tokenData, bool showExternalStreamsOnCreate = false);

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
        /// Clears just the "External services" toggle (flags3 0x02) for a UID, leaving every other
        /// flag untouched and bumping the revision like any flag write. Used when the user sets up
        /// their first stream addon: once AniSync can serve real streams, the external streaming-
        /// site links default off. No-op (no write, no revision bump) when the toggle is already
        /// off — so re-enabling it after a later addon add is respected. Returns true when a write
        /// actually happened.
        /// </summary>
        Task<bool> ClearShowExternalStreamsAsync(string uid);

        /// <summary>
        /// Removes the row for <paramref name="uid"/> from the config store. No-op if missing.
        /// Used by the "Delete Configuration" Danger Zone action.
        /// </summary>
        Task DeleteAsync(string uid);

        /// <summary>
        /// Rotates the install identifier: assigns the row a fresh random UID, preserving
        /// all of its data (tokens, linked accounts, flags, scrobble token, stream addons)
        /// and cascading the change to the per-user notification / watching-cache / push
        /// tables so those keep working under the new UID. Every old Stremio install URL,
        /// X-AniSync-Config header, and persisted UID cookie that referenced the previous
        /// UID immediately stops resolving — the rotation path for a leaked UID. Returns the
        /// new UID, or null when no row matched <paramref name="oldUid"/>.
        /// </summary>
        Task<string> RotateUidAsync(string oldUid);

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

        // ── Trakt ───────────────────────────────────────────────────────────
        // Trakt is now a first-class provider (AnimeService.Trakt): its credentials
        // live in the unified token model (primary token_json or a linked_tokens
        // entry), the same as every other provider. The legacy dedicated-column
        // storage is gone.

        /// <summary>
        /// Projects the connected Trakt credentials for this UID (whether held as the
        /// primary or a linked secondary) into the legacy TraktToken shape, or null when
        /// the user hasn't connected Trakt. Display-only convenience for the legacy video
        /// section — does not refresh. New code should resolve the Trakt token through the
        /// normal primary / linked-token paths.
        /// </summary>
        Task<TraktToken> GetTraktTokenAsync(string uid);

        /// <summary>
        /// Enumerates the UIDs of every config row that has Trakt connected — whether as the
        /// row's primary provider or a linked secondary. Backed by the <c>trakt_user_key</c>
        /// column (populated for both slots, with the unique partial index
        /// <c>idx_configs_trakt</c>), so it's an index scan rather than a full table walk.
        /// Used by the series-episode notifier to fan out per-user Trakt calendar reads.
        /// </summary>
        Task<List<string>> ListTraktConnectedUidsAsync();

        // ── Web-UI preferences (media-type modes + dashboard layout) ────────
        /// <summary>Reads the account's web-UI preferences (empty fields when unset/unknown UID).</summary>
        Task<WebSettings> GetWebSettingsAsync(string uid);
        /// <summary>Persists the enabled media-type set (comma-separated). No-op for an empty UID.</summary>
        Task SetEnabledMediaTypesAsync(string uid, string csv);
        /// <summary>Persists the dashboard layout JSON. No-op for an empty UID.</summary>
        Task SetDashboardLayoutAsync(string uid, string json);

        /// <summary>
        /// Reads the user's configured stream addons. Empty list when the UID
        /// is unknown or no addons are configured. Order is preserved (the
        /// Configure page renders them in addition order, which doubles as
        /// the user's priority).
        /// </summary>
        Task<List<StreamAddon>> GetStreamAddonsAsync(string uid);

        /// <summary>
        /// Appends an addon to the user's list. Returns true on success,
        /// false when the URL is empty or already in the list (idempotent
        /// — no error on duplicate add). The caller is expected to have
        /// validated the manifest URL first (via
        /// <see cref="IAddonStreamService.FetchManifestAsync"/>).
        /// </summary>
        Task<bool> AddStreamAddonAsync(string uid, StreamAddon addon);

        /// <summary>
        /// Removes the entry with the given manifest URL. Returns true
        /// when something was removed, false when no matching entry
        /// existed (unknown UID, unknown URL).
        /// </summary>
        Task<bool> RemoveStreamAddonAsync(string uid, string addonUrl);

        /// <summary>
        /// Reorders the stored stream-addon list to match the order of
        /// the supplied URL list. URLs not currently in the list are
        /// ignored; addons not in the supplied list keep their relative
        /// order and are appended at the end (defensive — shouldn't
        /// happen since the caller renders the list from the same
        /// source, but stops a stale client from accidentally dropping
        /// addons). Returns true when the list was changed.
        /// </summary>
        Task<bool> ReorderStreamAddonsAsync(string uid, IList<string> orderedUrls);

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
    }
}
