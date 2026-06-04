namespace AnimeList.Models.Api
{
    /// <summary>
    /// Request body for the API save endpoints (single + bulk). Distinct from the
    /// internal <see cref="Controllers.SaveEntryRequest"/> used by the Manage Entry
    /// page, which still accepts provider-native status strings for back-compat.
    ///
    /// <para><b>Id and season do NOT live in the body</b> for the single-entry
    /// endpoint — they're carried by the route (<c>/entries/{id}?season=</c>). The
    /// bulk endpoint uses <see cref="ApiBulkSaveEntry"/>, which carries id + season
    /// per-entry because there's no per-entry path segment.</para>
    /// </summary>
    public class ApiSaveEntryRequest
    {
        /// <summary>Canonical list status. Omit (or send null) to leave the existing
        /// status untouched server-side; send empty string to remove the entry from
        /// the user's list.</summary>
        public ListStatus? Status { get; set; }

        /// <summary>Episodes watched.</summary>
        public int Progress { get; set; }

        /// <summary>0–10 score. Provider-specific scales are normalised server-side.</summary>
        public double? Score { get; set; }

        /// <summary>Free-text notes attached to the entry.</summary>
        public string? Notes { get; set; }

        /// <summary>Number of times the user has rewatched the show.</summary>
        public int? RewatchCount { get; set; }

        /// <summary>Started watching date (ISO-8601 / yyyy-MM-dd).</summary>
        public string? StartedAt { get; set; }

        /// <summary>Finished watching date (ISO-8601 / yyyy-MM-dd).</summary>
        public string? FinishedAt { get; set; }
    }

    /// <summary>
    /// One row of the bulk-save body. Carries an id and optional season because
    /// the bulk endpoint has no per-entry path segment.
    /// </summary>
    public class ApiBulkSaveEntry : ApiSaveEntryRequest
    {
        /// <summary>Service-prefixed media id (<c>anilist:N</c>, <c>kitsu:N</c>, …).</summary>
        public string? Id { get; set; }

        /// <summary>Cour / season number for multi-cour franchises.</summary>
        public int? Season { get; set; }
    }
}
