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

        public List<int> Seasons { get; set; } = [];
        public int? SelectedSeason { get; set; }

        public AnimeService AnimeService { get; set; }

        public dynamic Videos { get; set; }
    }
}
