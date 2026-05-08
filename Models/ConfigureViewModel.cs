namespace AnimeList.Models
{
    /// <summary>
    /// Strongly-typed payload for the configure page (Views/Home/Index.cshtml). Wraps the
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

        // The Configuration model stores three flags as inverse-sense ("hide…", "disable…")
        // so default-zero installs keep the pre-feature behaviour. The configure UI exposes
        // them as positive toggles, so flip the bit here once.
        public bool ShowManageEntry => !Configuration.hideManageEntry;
        public bool AutoTrackProgress => !Configuration.disableAutoTrack;
        public bool GroupSeasons => !Configuration.disableSeasonGrouping;

        public Dictionary<AnimeService, LinkedToken> LinkedByService =>
            LinkedTokens.ToDictionary(t => t.Service, t => t);

        /// <summary>True when at least one linked account is healthy (not flagged for re-auth).</summary>
        public bool HasSwappableLinks => LinkedTokens.Any(t => !t.NeedsReauth);
    }
}
