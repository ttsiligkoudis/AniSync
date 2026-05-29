using System.Text;

namespace AnimeList.Services
{
    /// <summary>
    /// Curated catalog backing the "Quick setup" debrid flow on the
    /// /advanced Streams card. The user picks a debrid provider and pastes
    /// one API key; we turn that into ready-to-use Stremio manifest URLs
    /// for a small set of well-known scraper addons (Torrentio, Comet) so
    /// they don't have to walk each addon's own /configure page by hand and
    /// paste the resulting URLs back in one at a time.
    ///
    /// Every URL this produces is still run through
    /// <see cref="Interfaces.IAddonStreamService.FetchManifestAsync"/>
    /// before it's persisted, so a drifted upstream config format degrades
    /// to "addon skipped" rather than a saved-but-broken entry — the manual
    /// paste box stays as the fallback for anything this catalog can't
    /// build (notably MediaFusion, whose config is AES-encrypted with the
    /// addon's own server-side secret and so can't be minted client-side).
    /// </summary>
    public static class StreamAddonCatalog
    {
        /// <summary>
        /// One debrid service the quick-setup dropdown offers.
        /// <see cref="TorrentioKey"/> is the option token Torrentio expects
        /// in its flat path config (<c>realdebrid=KEY</c>);
        /// <see cref="CometService"/> is the <c>debridService</c> value
        /// Comet expects inside its base64 config blob. They happen to
        /// match for every provider today, but are kept as separate fields
        /// so a future upstream divergence is a one-line data change here
        /// rather than a code change.
        /// </summary>
        public record DebridProvider(
            string Id,
            string DisplayName,
            string TorrentioKey,
            string CometService,
            string ApiKeyUrl);

        /// <summary>An addon the quick-setup flow knows how to auto-configure.</summary>
        public record CatalogAddon(string Id, string DisplayName);

        // ApiKeyUrl is the page where the user generates / copies the token
        // for that service — the "Get your API key" link jumps straight
        // there in a new tab based on the dropdown selection.
        public static readonly IReadOnlyList<DebridProvider> Providers = new[]
        {
            new DebridProvider("realdebrid", "Real-Debrid", "realdebrid", "realdebrid", "https://real-debrid.com/apitoken"),
            new DebridProvider("alldebrid",  "AllDebrid",   "alldebrid",  "alldebrid",  "https://alldebrid.com/apikeys/"),
            new DebridProvider("premiumize", "Premiumize",  "premiumize", "premiumize", "https://www.premiumize.me/account"),
            new DebridProvider("torbox",     "TorBox",      "torbox",     "torbox",     "https://torbox.app/settings"),
            new DebridProvider("debridlink", "Debrid-Link", "debridlink", "debridlink", "https://debrid-link.com/webapp/apikey"),
            new DebridProvider("easydebrid", "EasyDebrid",  "easydebrid", "easydebrid", "https://paradise-cloud.com/products/easydebrid"),
            new DebridProvider("offcloud",   "Offcloud",    "offcloud",   "offcloud",   "https://offcloud.com/#/account"),
        };

        public static readonly IReadOnlyList<CatalogAddon> Addons = new[]
        {
            new CatalogAddon("torrentio", "Torrentio"),
            new CatalogAddon("comet",     "Comet"),
        };

        public static DebridProvider FindProvider(string id) =>
            string.IsNullOrWhiteSpace(id)
                ? null
                : Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        public static bool IsKnownAddon(string addonId) =>
            !string.IsNullOrWhiteSpace(addonId)
            && Addons.Any(a => string.Equals(a.Id, addonId, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Builds the Stremio manifest URL for one catalog addon, given a
        /// debrid provider + API key. Pure string construction, no network —
        /// the caller validates each URL through the addon-stream service
        /// before persisting. Returns null for an unknown addon id (or a
        /// missing provider / key) so the caller can skip it.
        /// </summary>
        public static string BuildManifestUrl(string addonId, DebridProvider provider, string apiKey)
        {
            if (provider == null || string.IsNullOrWhiteSpace(apiKey)) return null;
            var key = apiKey.Trim();

            return (addonId ?? string.Empty).ToLowerInvariant() switch
            {
                "torrentio" => BuildTorrentio(provider, key),
                "comet"     => BuildComet(provider, key),
                _           => null,
            };
        }

        // Torrentio reads its config from flat pipe-separated key=value
        // segments in the URL path. The debrid key alone is the minimal
        // valid config — Torrentio applies its own default provider set and
        // sort order on top, exactly as its /configure page does when you
        // only fill in the debrid field. The API key isn't percent-encoded:
        // debrid tokens are URL-safe alphanumerics and Torrentio parses the
        // raw path segment, so escaping it would corrupt the value.
        private static string BuildTorrentio(DebridProvider provider, string key)
            => $"https://torrentio.strem.fun/{provider.TorrentioKey}={key}/manifest.json";

        // Comet reads a base64-encoded JSON config blob from the path,
        // mirroring exactly what its own /configure page emits:
        // JSON.stringify → base64 → percent-encode the segment. The shape
        // below tracks current Comet (the debrid credential lives in a
        // `debridServices` array of { service, apiKey } objects — the old
        // flat debridService/debridApiKey pair was dropped). Values are
        // neutral defaults (no quality cap, cached + uncached both shown);
        // the user can fine-tune later via the Configure link on the row.
        // base64 can contain '+' and '/', so the segment is percent-encoded
        // before it goes in the path; Comet URL-decodes it back before
        // base64-decoding.
        private static string BuildComet(DebridProvider provider, string key)
        {
            var config = new
            {
                maxResultsPerResolution = 0,
                maxSize = 0,
                cachedOnly = false,
                sortCachedUncachedTogether = false,
                removeTrash = true,
                resultFormat = new[] { "all" },
                debridServices = new[]
                {
                    new { service = provider.CometService, apiKey = key },
                },
                enableTorrent = false,
                deduplicateStreams = true,
                scrapeDebridAccountTorrents = false,
                debridStreamProxyPassword = "",
                languages = new
                {
                    required = Array.Empty<string>(),
                    allowed = Array.Empty<string>(),
                    exclude = Array.Empty<string>(),
                    preferred = Array.Empty<string>(),
                },
                resolutions = new { },
                options = new
                {
                    remove_ranks_under = -10000000000L,
                    allow_english_in_languages = false,
                    remove_unknown_languages = false,
                },
            };
            var json = SerializeObject(config);
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            return $"https://comet.elfhosted.com/{Uri.EscapeDataString(b64)}/manifest.json";
        }
    }
}
