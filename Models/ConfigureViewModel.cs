namespace AnimeList.Models
{
    /// <summary>
    /// Strongly-typed payload for the configure page (Views/Home/Configure.cshtml). Wraps the
    /// persisted <see cref="Configuration"/> alongside the per-request identity bits the
    /// view needs to render the correct UI mode (login vs. configure, primary vs. linked
    /// pills, install URL bytes, etc.). Everything here used to live as ViewBag entries —
    /// this type just gives them a contract.
    /// </summary>
    public class ConfigureViewModel
    {
        /// <summary>
        /// base64url v3 inline token bytes for anonymous installs. Mutually exclusive with
        /// <see cref="ConfigUid"/>: exactly one of the two is non-empty for a logged-in user.
        /// </summary>
        public string TokenData { get; init; }

        /// <summary>22-char base64url UID pointing at a row in the config store.</summary>
        public string ConfigUid { get; init; }

        /// <summary>
        /// Cache-busting counter appended to the v5 install URL. Bumped on every SaveConfig
        /// so Stremio refetches the manifest after a flag change.
        /// </summary>
        public long ConfigRevision { get; init; }

        /// <summary>The user's primary provider — drives the login button and active pill.</summary>
        public AnimeService AnimeService { get; init; } = AnimeService.Kitsu;

        /// <summary>True for "Continue without account" installs (v3 inline token).</summary>
        public bool AnonymousUser { get; init; }

        /// <summary>Linked secondary providers (multi-provider sync targets).</summary>
        public List<LinkedToken> LinkedTokens { get; init; } = new();

        /// <summary>
        /// Per-user webhook bearer for Plex/Jellyfin/Emby scrobble ingestion. Lazily generated
        /// the first time the user opens the configure page — null only for not-logged-in /
        /// anonymous installs (where home-server sync doesn't apply anyway).
        /// </summary>
        public string ScrobbleToken { get; init; }

        /// <summary>
        /// Optional Plex Home username. When set, scrobble events from Plex whose
        /// <c>Account.title</c> doesn't match are silently dropped — handles the shared-server
        /// case where roommates' viewing should not scrobble onto this user's trackers.
        /// </summary>
        public string PlexUsername { get; init; }

        /// <summary>
        /// Presence-only flag for the user's Real-Debrid API key. The plaintext
        /// value is never re-emitted to the client after first save — the
        /// configure UI renders a "•••••• (set)" badge + Replace button when
        /// this is true, the entry input otherwise.
        /// </summary>
        public bool HasRealDebridKey { get; init; }

        /// <summary>
        /// Persisted toggle flags. Always non-null — the controller hands the view a
        /// default-initialised instance when nothing is loaded yet.
        /// </summary>
        public Configuration Configuration { get; init; } = new();

        // Derived helpers — keep these here rather than recomputing in the .cshtml so the
        // view stays declarative.

        public bool IsLoggedIn => !string.IsNullOrEmpty(TokenData) || !string.IsNullOrEmpty(ConfigUid);
        public bool IsAnilist => AnimeService == AnimeService.Anilist;
        public bool IsMal => AnimeService == AnimeService.MyAnimeList;
        public bool IsKitsu => !IsAnilist && !IsMal;

        /// <summary>Numeric service id consumed by the JS picker (matches the AnimeService enum order).</summary>
        public int ServiceIndex => IsAnilist ? 1 : IsMal ? 2 : 0;

        public string ServiceDisplay => IsAnilist ? "AniList" : IsMal ? "MyAnimeList" : "Kitsu";

        // hideManageEntry / disableAutoTrack are inverse-sense — flip them so the
        // configure UI can render positive toggles ("Manage Entry on" / "Auto-track on").
        // enableSeasonGrouping is already positive-sense (default OFF for new users),
        // so it passes through unchanged.
        public bool ShowManageEntry => !Configuration.hideManageEntry;
        public bool AutoTrackProgress => !Configuration.disableAutoTrack;
        public bool GroupSeasons => Configuration.enableSeasonGrouping;
        public bool HideUnreleasedWatching => Configuration.hideUnreleasedFromWatching;

        public Dictionary<AnimeService, LinkedToken> LinkedByService =>
            LinkedTokens.ToDictionary(t => t.Service, t => t);

        /// <summary>True when at least one linked account is healthy (not flagged for re-auth).</summary>
        public bool HasSwappableLinks => LinkedTokens.Any(t => !t.NeedsReauth);
    }
}
