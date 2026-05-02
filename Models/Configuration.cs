namespace AnimeList.Models
{
    public class Configuration
    {
        /// <summary>
        /// Inline token JSON (set for legacy v1/v2/v3 install URLs that embed credentials in
        /// the URL itself). Mutually exclusive with <see cref="tokenUid"/>: v4 URLs leave this
        /// null and store the token JSON in <see cref="Services.Interfaces.IConfigStore"/>.
        /// </summary>
        public string tokenData  { get; set; }

        /// <summary>
        /// 22-char base64url UID pointing at a row in <see cref="Services.Interfaces.IConfigStore"/>.
        /// Set for v4 and v5 install URLs.
        /// </summary>
        public string tokenUid { get; set; }

        /// <summary>
        /// True for v5 URLs (UID only, flags persisted in the config store). When set, callers
        /// must hydrate the flag fields below from
        /// <see cref="Services.Interfaces.IConfigStore.GetFlagsAsync"/> before reading them —
        /// see <see cref="Utils.ResolveConfigAsync"/>.
        /// </summary>
        public bool flagsInDb { get; set; }

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
    }
}
