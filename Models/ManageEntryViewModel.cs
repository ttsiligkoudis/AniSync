namespace AnimeList.Models
{
    /// <summary>
    /// View model for the Manage Entry page, holding anime metadata and the user's list entry state.
    /// </summary>
    public class ManageEntryViewModel
    {
        public string Id { get; set; }
        public string Config { get; set; }
        public string Name { get; set; }
        public string Poster { get; set; }
        public string Type { get; set; }

        public string Status { get; set; }
        public int Progress { get; set; }
        public int? TotalEpisodes { get; set; }

        public double? Score { get; set; }
        public string Notes { get; set; }
        public int RewatchCount { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        public List<EntrySeason> Seasons { get; set; } = [];

        /// <summary>
        /// The resolved entry id for the season auto-selected on page load. Matches one of
        /// <see cref="Seasons"/>'s ids (or equals <see cref="Id"/> when there's no season
        /// dropdown). The dropdown's &lt;option value="…"&gt; carries the same shape so the
        /// JS can send it straight to GetEntry/SaveEntry.
        /// </summary>
        public string SelectedEntryId { get; set; }

        public AnimeService AnimeService { get; set; }

        public dynamic Videos { get; set; }
    }
}
