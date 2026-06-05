namespace AnimeList.Models.Api
{
    /// <summary>
    /// Body for POST /api/v1/me/mark-watched — the header-authed twin of the MVC
    /// MetaController.MarkWatched body. Marks an anime episode watched on the user's
    /// primary tracker + linked secondaries (or routes a movie / series to Trakt
    /// history). Fired by the watch page at the 70 % progress mark and on the
    /// external "Open with…" hand-off.
    /// </summary>
    public class ApiMarkWatchedRequest
    {
        /// <summary>Service-prefixed anime id (anilist:/mal:/kitsu:) or, for a
        /// Cinemeta video, the IMDb tt id.</summary>
        public string? Id { get; set; }

        /// <summary>Cour / season for multi-cour franchises; null on single-season.</summary>
        public int? Season { get; set; }

        /// <summary>Episode number (within the chosen season).</summary>
        public int Episode { get; set; }

        /// <summary>"movie" / "series" when the watch page is a Cinemeta video (Id is
        /// an IMDb tt id) — routes the mark to the user's Trakt history instead of the
        /// anime primary. Null / empty for the anime watch page.</summary>
        public string? Type { get; set; }

        /// <summary>Optional source URL. When present the server probes it (Range 0-0)
        /// and refuses to mark if the response looks like a debrid DMCA placeholder
        /// (small total size). Guards the external-launch trigger against
        /// false-marking a known-bad source.</summary>
        public string? SourceUrl { get; set; }
    }

    /// <summary>
    /// Body for POST /api/v1/me/scrobble-progress — the watch player's page-leave
    /// beacon that parks a movie / series position in Trakt's Continue Watching.
    /// Video-only (anime tracks via its own primary). Header-authed twin of the MVC
    /// MetaController.ScrobbleProgress body.
    /// </summary>
    public class ApiScrobbleProgressRequest
    {
        /// <summary>IMDb tt id of the movie / series.</summary>
        public string? Id { get; set; }

        /// <summary>"movie" / "series" (video only — anime is rejected).</summary>
        public string? Type { get; set; }

        /// <summary>Cour / season for series; null on single-season (defaults to 1 server-side).</summary>
        public int? Season { get; set; }

        /// <summary>Episode number for series; ignored for movies.</summary>
        public int Episode { get; set; }

        /// <summary>Playback progress as a 0-100 percentage.</summary>
        public double Progress { get; set; }
    }
}
