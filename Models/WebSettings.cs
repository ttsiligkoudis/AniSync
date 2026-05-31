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
    }
}
