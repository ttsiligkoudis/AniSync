namespace AnimeList.Models.Api
{
    /// <summary>
    /// Canonical list-status vocabulary used by the API. Provider-specific spellings
    /// (AniList <c>CURRENT</c>, Kitsu <c>current</c>, MAL <c>watching</c> for the
    /// "currently watching" state) are translated server-side via
    /// <see cref="Utils.TranslateStatusForService(string, TokenData)"/>; clients use
    /// these names regardless of which provider is primary.
    /// </summary>
    public enum ListStatus
    {
        /// <summary>Currently watching.</summary>
        Watching,
        /// <summary>Completed.</summary>
        Completed,
        /// <summary>Plan to watch.</summary>
        Planning,
        /// <summary>On hold.</summary>
        Paused,
        /// <summary>Dropped.</summary>
        Dropped,
        /// <summary>Rewatching. Degrades to "currently watching" on Kitsu (no native
        /// rewatch state) and to <c>is_rewatching=true</c> on MAL.</summary>
        Rewatching,
    }
}
