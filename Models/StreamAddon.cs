namespace AnimeList.Models
{
    /// <summary>
    /// One configured Stremio-compatible stream addon. The user pastes a
    /// manifest URL (Torrentio / MediaFusion / Comet / Jackettio /
    /// AIOStreams / …); we GET it once on save to pull the addon's
    /// display name, then store the pair. Episode lookups fan out across
    /// every entry in this list in parallel.
    /// </summary>
    public record StreamAddon
    {
        /// <summary>
        /// Full manifest URL ending in <c>/manifest.json</c>. Stored
        /// verbatim so addon-specific config segments (Torrentio's
        /// flat <c>realdebrid=KEY</c> string, MediaFusion's encrypted
        /// blob, etc.) flow through transparently.
        /// </summary>
        public string Url { get; init; }

        /// <summary>
        /// Display name pulled from the addon's <c>manifest.json</c> on
        /// save (e.g. "Torrentio", "MediaFusion | ElfHosted"). Falls
        /// back to the URL's host segment if the manifest doesn't
        /// expose a name.
        /// </summary>
        public string Name { get; init; }
    }
}
