namespace AnimeList.Models
{
    /// <summary>
    /// A single entry the user has chosen to hide from their Discover catalogs.
    /// Scoped per-config (UID) and keyed by the provider-prefixed entry id. The
    /// display fields (<see cref="Title"/> / <see cref="ImageUrl"/> /
    /// <see cref="MediaType"/>) are cached at write time so the Hidden section
    /// can render the card without re-fetching the provider for every poster.
    /// </summary>
    public class HiddenEntry
    {
        /// <summary>Provider-prefixed entry id, e.g. <c>anilist:21</c>.</summary>
        public string Id { get; set; }

        /// <summary>Cached display title at hide time.</summary>
        public string Title { get; set; }

        /// <summary>Cached poster URL at hide time (may be null).</summary>
        public string ImageUrl { get; set; }

        /// <summary>Media type: <c>anime</c> / <c>movie</c> / <c>series</c>.</summary>
        public string MediaType { get; set; }

        /// <summary>Unix seconds the entry was hidden — drives the Hidden
        /// section's most-recently-hidden-first ordering.</summary>
        public long CreatedAt { get; set; }
    }
}
