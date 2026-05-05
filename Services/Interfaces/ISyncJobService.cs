using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Background full-library sync orchestrator. The user kicks one of these off from the
    /// configure page after first linking a provider; the job walks the primary's whole
    /// library and pushes every entry through the regular sync fan-out so the freshly-linked
    /// account catches up. Status is tracked in memory keyed on the install's UID — the job
    /// dies if the Fly machine recycles, which is a known limitation of the MVP.
    /// </summary>
    public interface ISyncJobService
    {
        /// <summary>
        /// Starts a background sync for the supplied UID. Returns false if a sync is already
        /// running for that UID — callers should poll <see cref="GetStatus"/> instead of
        /// kicking a new one off.
        /// </summary>
        bool TryStart(string uid, TokenData primary);

        /// <summary>
        /// Returns the current status for a UID's sync job (or null when no job has ever
        /// run for that UID). The flag <see cref="SyncJobStatus.Running"/> is the source of
        /// truth for "is the worker still alive"; counters are updated as it progresses.
        /// </summary>
        SyncJobStatus GetStatus(string uid);
    }

    public class SyncJobStatus
    {
        public bool Running { get; set; }
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        /// <summary>
        /// Free-form last-action text — surfaced in the UI's progress strip. Examples:
        /// "Fetching primary library…", "Syncing 42 / 247", "Done", "Failed: …".
        /// </summary>
        public string Message { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }
}
