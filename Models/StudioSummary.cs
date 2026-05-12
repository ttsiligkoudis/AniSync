namespace AnimeList.Models
{
    /// <summary>
    /// Slim studio entry — id + display name + anime count — surfaced by
    /// the /studio listing page. Per-studio detail is fetched separately
    /// by <see cref="Services.Interfaces.IAnilistFallback.GetStudioMediaAsync"/>
    /// when the user opens a tile. AnimeCount is the upstream
    /// Studio.media(type: ANIME).pageInfo.total — used to render a
    /// "· N anime" hint on the tile so a glance at the grid hints at
    /// catalog size before clicking through.
    /// </summary>
    public class StudioSummary
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int AnimeCount { get; set; }
    }
}
