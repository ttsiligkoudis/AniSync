namespace AnimeList.Models
{
    /// <summary>
    /// Per-target outcome from <c>SyncService.FanOutSaveAsync</c>. Background paths
    /// (scrobble webhook ingestion, sync backfill) discard this — they treat fan-out
    /// as best-effort and log failures internally. Foreground paths (the Manage Entry
    /// modal's Save, the +1 episode bump) surface <see cref="Failed"/> as a partial-
    /// success warning so the user sees "Saved on AniList, MAL failed" instead of a
    /// uniform green toast that hides the breakage.
    /// </summary>
    public class FanOutSaveResult
    {
        public List<AnimeService> Succeeded { get; init; } = new();
        public List<AnimeService> Failed { get; init; } = new();

        public bool HasFailures => Failed.Count > 0;

        public static readonly FanOutSaveResult Empty = new();
    }
}
