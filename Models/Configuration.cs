namespace AnimeList.Models
{
    public class Configuration
    {
        /// <summary>
        /// Inline token JSON, set for v3 install URLs that embed credentials in the URL
        /// itself (the anonymous-install path — no UID to look up). Mutually exclusive with
        /// <see cref="tokenUid"/>: v5 URLs store the token JSON in
        /// <see cref="Services.Interfaces.IConfigStore"/> instead and leave this null.
        /// </summary>
        public string tokenData  { get; set; }

        /// <summary>
        /// 22-char base64url UID pointing at a row in <see cref="Services.Interfaces.IConfigStore"/>.
        /// Set for v5 install URLs; null for v3 anonymous installs. Presence of this field is
        /// the signal that toggle flags should be hydrated from the store via
        /// <see cref="Utils.ResolveConfigAsync"/>.
        /// </summary>
        public string tokenUid { get; set; }

        public bool showCurrent { get; set; }
        public bool showCompleted { get; set; }
        public bool showTrending { get; set; }
        public bool showSeasonal { get; set; }
        public bool showPlanning { get; set; }
        public bool showPaused { get; set; }
        public bool showDropped { get; set; }
        public bool showRepeating { get; set; }
        public bool showAiring { get; set; }
        public bool discoverOnlyCurrent { get; set; }
        public bool discoverOnlyCompleted { get; set; }
        public bool discoverOnlyTrending { get; set; }
        public bool discoverOnlySeasonal { get; set; }
        public bool discoverOnlyPlanning { get; set; }
        public bool discoverOnlyPaused { get; set; }
        public bool discoverOnlyDropped { get; set; }
        public bool discoverOnlyRepeating { get; set; }
        public bool discoverOnlyAiring { get; set; }
        public bool showExternalStreams { get; set; }

        // Inverse-sense bits: stored 0/false by default so existing rows keep doing what
        // they did before this flag existed. The configure-page UI exposes them as positive
        // toggles ("Manage Entry", "Auto-track progress") with default checked, and the
        // JS flips the bit when the toggle is unchecked.
        public bool hideManageEntry { get; set; }
        public bool disableAutoTrack { get; set; }
        // Positive-sense: default-zero installs (and anonymous viewers) get NO season
        // grouping — every cour shows as its own card. Set the bit to opt into grouping,
        // which collapses every cour of a franchise into a single IMDb-keyed entry.
        // Bit 0x20 used to be the inverted "disableSeasonGrouping" pref; flipped to
        // positive-sense so a freshly-built Configuration defaults to "grouping off"
        // without any extra controller-level branching.
        public bool enableSeasonGrouping { get; set; }

        // Hide entries whose upstream status is "not yet aired" from the Currently
        // Watching catalog on the site and in Stremio. Default off — pre-release
        // entries manually moved to Watching stay visible unless the user opts in
        // to filtering them out via the /configure Site Preferences toggle.
        public bool hideUnreleasedFromWatching { get; set; }
    }
}
