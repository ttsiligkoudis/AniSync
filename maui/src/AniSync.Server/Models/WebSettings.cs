namespace AnimeList.Models
{
    /// <summary>
    /// Per-account web-UI preferences persisted in the configs row (separate
    /// from the Stremio addon config). Mirrors what the browser keeps in
    /// localStorage so the settings follow the user across devices.
    /// </summary>
    public class WebSettings
    {
        /// <summary>Comma-separated enabled mode set (e.g. "anime,movie,series"); null when unset.</summary>
        public string EnabledMediaTypes { get; set; }
        /// <summary>Dashboard section order + visibility as JSON; null when unset.</summary>
        public string DashboardLayout { get; set; }
        /// <summary>Preferred default AUDIO language (ISO 639-1, e.g. "en"); null = English default.
        /// Used by the native player to preselect an audio track.</summary>
        public string DefaultAudioLanguage { get; set; }
        /// <summary>Preferred default SUBTITLE language (ISO 639-1, e.g. "en"); null = English default.
        /// Used by both players + the web embedded-subtitle worker.</summary>
        public string DefaultSubtitleLanguage { get; set; }
    }
}
