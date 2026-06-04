using AnimeList.Models;

namespace AnimeList.Services.Interfaces
{
    /// <summary>
    /// Reads a user's list for a given status across their primary service
    /// AND every healthy linked secondary, in parallel, then merges the
    /// results into a single deduped list of <see cref="Meta"/> cards.
    ///
    /// Backs both the /library grid and the dashboard's Continue Watching
    /// shelf so an anime the user tracks only on a linked provider (e.g. an
    /// AniList-only entry for an MAL-primary user) still surfaces in both.
    /// Dedup runs primary-first: the primary's view of an anime wins on
    /// collision, with id-space mapping as the dedup key and a title-
    /// similarity safety net for entries the cross-service mapping can't
    /// link.
    /// </summary>
    public interface IMergedListService
    {
        Task<List<Meta>> GetMergedListAsync(
            TokenData primary, string uid, ListType listType,
            string genre = null, bool groupSeasons = false,
            bool hideUnreleased = false, bool hideAdult = false);
    }
}
