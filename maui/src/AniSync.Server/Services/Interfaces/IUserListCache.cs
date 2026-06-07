using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// NOTE: despite the legacy name, this is no longer a list cache. The in-memory per-user list cache was
    /// removed — its only consumer (the dashboard "Continue watching" shelf) now fetches live, so a cached
    /// "Watching" list couldn't go stale behind an out-of-band scrobble. What remains is the edit hook: a
    /// user changing their list through the app UI marks their persistent watching cache stale so the
    /// episode-notification dispatcher + calendar re-fetch promptly instead of waiting for the daily
    /// backstop. (Name + method kept to avoid churning every call site; a rename is a safe future cleanup.)
    /// </summary>
    public interface IUserListCache
    {
        /// <summary>
        /// Marks the user's persistent watching cache stale after an in-app list edit, so the episode
        /// dispatcher + calendar re-fetch their "Watching" set on the next pass instead of waiting for the
        /// daily backstop. Best-effort + self-correcting; no-op for anonymous tokens.
        /// </summary>
        void Invalidate(TokenData token);
    }
}
