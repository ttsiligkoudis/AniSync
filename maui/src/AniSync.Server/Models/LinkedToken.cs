namespace AnimeList.Models
{
    /// <summary>
    /// A non-primary provider account the user has linked to their AniSync install. Writes
    /// (Manage Entry save / delete, auto-track) fan out from the primary provider to every
    /// linked one so multiple anime-list accounts stay in sync without manual juggling.
    /// </summary>
    public class LinkedToken
    {
        public AnimeService Service { get; set; }
        public TokenData TokenData { get; set; }
        /// <summary>
        /// Set when a token refresh fails for a linked provider. The UI surfaces a "needs
        /// re-auth" badge so the user can re-link without us silently dropping their writes.
        /// </summary>
        public bool NeedsReauth { get; set; }
    }
}
