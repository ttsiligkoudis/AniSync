using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Per-user store of entries hidden from Discover. Backed by the
    /// <c>hidden_entries</c> table (created by <see cref="SqliteConfigStore"/>'s
    /// schema), keyed by a composite (uid, id). Display fields are cached on
    /// write so the Hidden section renders without re-hitting the providers.
    /// </summary>
    public interface IHiddenEntryStore
    {
        /// <summary>Inserts (or refreshes the cached display fields of) a hidden
        /// entry for the user. Idempotent on (uid, id).</summary>
        Task AddAsync(string uid, HiddenEntry entry);

        /// <summary>Removes a hidden entry. Returns true if a row was deleted.</summary>
        Task<bool> RemoveAsync(string uid, string id);

        /// <summary>Whether the given entry id is hidden for the user.</summary>
        Task<bool> IsHiddenAsync(string uid, string id);

        /// <summary>Every hidden entry id for the user, for stripping them out of
        /// Discover catalog results. Empty set for anonymous / unknown uids.</summary>
        Task<HashSet<string>> GetHiddenIdsAsync(string uid);

        /// <summary>A page of the user's hidden entries, most-recently-hidden
        /// first, for the Discover Hidden section's infinite scroll.</summary>
        Task<List<HiddenEntry>> GetPageAsync(string uid, int limit, int offset);
    }
}
