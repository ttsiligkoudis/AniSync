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
            string MediaFusionService,
            string ApiKeyUrl);

        /// <summary>An addon the quick-setup flow knows how to auto-configure.</summary>
        public record CatalogAddon(string Id, string DisplayName);

        // ApiKeyUrl is the page where the user generates / copies the token
        // for that service — the "Get your API key" link jumps straight
        // there in a new tab based on the dropdown selection.
        public static readonly IReadOnlyList<DebridProvider> Providers = new[]
        {
            new DebridProvider("realdebrid", "Real-Debrid", "realdebrid", "realdebrid", "realdebrid", "https://real-debrid.com/apitoken"),
            new DebridProvider("alldebrid",  "AllDebrid",   "alldebrid",  "alldebrid",  "alldebrid",  "https://alldebrid.com/apikeys/"),
            new DebridProvider("premiumize", "Premiumize",  "premiumize", "premiumize", "premiumize", "https://www.premiumize.me/account"),
            new DebridProvider("torbox",     "TorBox",      "torbox",     "torbox",     "torbox",     "https://torbox.app/settings"),
            new DebridProvider("debridlink", "Debrid-Link", "debridlink", "debridlink", "debridlink", "https://debrid-link.com/webapp/apikey"),
            new DebridProvider("easydebrid", "EasyDebrid",  "easydebrid", "easydebrid", "easydebrid", "https://paradise-cloud.com/products/easydebrid"),
            new DebridProvider("offcloud",   "Offcloud",    "offcloud",   "offcloud",   "offcloud",   "https://offcloud.com/#/account"),
        };

        public static readonly IReadOnlyList<CatalogAddon> Addons = new[]
        {
            new CatalogAddon("torrentio",   "Torrentio"),
            new CatalogAddon("comet",       "Comet"),
            new CatalogAddon("mediafusion", "MediaFusion"),
        };

        // MediaFusion encrypts its config server-side with a private key, so
        // unlike Torrentio / Comet we can't mint its manifest URL offline —
        // the config JSON is POSTed to this endpoint, which returns an
        // encrypted token we drop into {host}/{token}/manifest.json.
        public const string MediaFusionHost = "https://mediafusion.elfhosted.com";
        public const string MediaFusionEncryptUrl = MediaFusionHost + "/encrypt-user-data";

        public static DebridProvider FindProvider(string id) =>
            string.IsNullOrWhiteSpace(id)
                ? null
                : Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        public static bool IsKnownAddon(string addonId) =>
            !string.IsNullOrWhiteSpace(addonId)
            && Addons.Any(a => string.Equals(a.Id, addonId, StringComparison.OrdinalIgnoreCase));

        // Comet runs as two well-known public instances; we set up both so
        // episode lookups fan out across the pair (one is often reachable /
        // populated when the other is rate-limited or down). They share the
        // same config blob — only the host differs — and surface as two
        // distinct rows because each manifest reports its own display name.
        private static readonly string[] CometHosts =
        {
            "https://comet.elfhosted.com",
            "https://comet.feels.legal",
        };

        /// <summary>
        /// Builds the Stremio manifest URL(s) for one catalog addon, given a
        /// debrid provider + API key. Pure string construction, no network —
        /// the caller validates each URL through the addon-stream service
        /// before persisting. Returns one URL for most addons, but more than
        /// one where an addon runs as several interchangeable public
        /// instances (Comet). Empty for an unknown addon id (or a missing
        /// provider / key) so the caller can skip it.
        /// </summary>
        public static IReadOnlyList<string> BuildManifestUrls(string addonId, DebridProvider provider, string apiKey)
        {
            if (provider == null || string.IsNullOrWhiteSpace(apiKey)) return Array.Empty<string>();
            var key = apiKey.Trim();

            return (addonId ?? string.Empty).ToLowerInvariant() switch
            {
                "torrentio" => new[] { BuildTorrentio(provider, key) },
                "comet"     => BuildComet(provider, key),
                _           => Array.Empty<string>(),
            };
        }

        // Torrentio reads its config from pipe-separated key=value segments
        // in the URL path, with the pipes percent-encoded as %7C exactly as
        // its own /configure page emits them. We bake in a curated default
        // set — sort by seeders, drop cam / screener / 480p junk via
        // qualityfilter, cap at 2 results per resolution (Torrentio's `limit`
        // is a per-quality cap, matching the per-resolution limit we set on
        // Comet / MediaFusion), and trim the catalog + download-link noise from
        // the debrid output — then append the debrid credential as the final
        // segment. No language priority: left neutral so the default suits any
        // user. The API key isn't escaped: debrid tokens are URL-safe
        // alphanumerics and Torrentio parses the raw segment, so escaping would
        // corrupt it.
        private const string TorrentioOptions =
            "sort=seeders%7Cqualityfilter=480p,scr,cam%7Climit=2%7Cdebridoptions=nocatalog,nodownloadlinks";

        private static string BuildTorrentio(DebridProvider provider, string key)
            => $"https://torrentio.strem.fun/{TorrentioOptions}%7C{provider.TorrentioKey}={key}/manifest.json";

        // Comet reads a base64-encoded JSON config blob from the path,
        // mirroring exactly what its own /configure page emits:
        // JSON.stringify → btoa (raw standard base64) → drop it straight in
        // the path, no percent-encoding (confirmed against a real Comet URL,
        // whose blob is bare base64). The shape below tracks current Comet:
        // the debrid credential lives in a `debridServices` array of
        // { service, apiKey } objects (the old flat debridService/
        // debridApiKey pair was dropped). cachedOnly is on so only
        // instantly-playable debrid sources surface; everything else is left
        // at Comet's defaults and can be fine-tuned via the Configure link on
        // the row. Built once and pointed at every Comet instance.
        private static IReadOnlyList<string> BuildComet(DebridProvider provider, string key)
        {
            var config = new
            {
                // Cap streams to 2 per resolution (0 would be unlimited),
                // matching the per-resolution limit on Torrentio / MediaFusion.
                maxResultsPerResolution = 2,
                maxSize = 0,
                cachedOnly = true,
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
                // Best-guess shape for Comet's resolution picker: a dict of
                // resolution-key → enabled. Restricts to 2160p/1440p/1080p/
                // 720p (+ Unknown) and drops the SD/LD tiers, mirroring the
                // selection in Comet's UI. Keys lead with digits so this has
                // to be a Dictionary, not an anonymous object. If Comet
                // actually expects a different shape it'll fall back to its
                // own defaults rather than break the blob.
                resolutions = new Dictionary<string, bool>
                {
                    ["2160p"] = true,
                    ["1440p"] = true,
                    ["1080p"] = true,
                    ["720p"] = true,
                    ["576p"] = false,
                    ["480p"] = false,
                    ["360p"] = false,
                    ["240p"] = false,
                    ["unknown"] = true,
                },
                options = new
                {
                    remove_ranks_under = -10000000000L,
                    allow_english_in_languages = false,
                    remove_unknown_languages = false,
                },
            };
            var json = SerializeObject(config);
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            return CometHosts.Select(host => $"{host}/{b64}/manifest.json").ToArray();
        }

        /// <summary>
        /// Builds the JSON body to POST to MediaFusion's
        /// <see cref="MediaFusionEncryptUrl"/>. Unlike Torrentio / Comet,
        /// MediaFusion encrypts the config server-side, so this is only half
        /// the job — the caller sends this body to the encrypt endpoint and
        /// drops the returned token into {host}/{token}/manifest.json. Shape
        /// mirrors what MediaFusion's own configure page submits: one debrid
        /// streaming-provider (cached-only, via MediaFlow), high-tier
        /// resolutions + Unknown only, anime included, all-permissive
        /// content filters. Returns null for a missing provider / key.
        /// </summary>
        public static string BuildMediaFusionConfigJson(DebridProvider provider, string apiKey)
        {
            if (provider == null || string.IsNullOrWhiteSpace(apiKey)) return null;
            var key = apiKey.Trim();

            // One streaming-provider entry, shared by the array form (current
            // MediaFusion) and the singular field (kept for back-compat).
            var prov = new
            {
                name = "Provider",
                service = provider.MediaFusionService,
                token = key,
                enable_watchlist_catalogs = false,
                qbittorrent_config = (object)null,
                only_show_cached_streams = true,
                use_mediaflow = true,
                sabnzbd_config = (object)null,
                nzbget_config = (object)null,
                nzbdav_config = (object)null,
                easynews_config = (object)null,
                priority = 0,
                enabled = true,
            };

            var config = new
            {
                streaming_providers = new[] { prov },
                streaming_provider = prov,
                selected_catalogs = Array.Empty<string>(),
                // High tiers + Unknown (the trailing null) only, mirroring
                // the resolution restriction applied to Comet.
                selected_resolutions = new[] { "4k", "2160p", "1440p", "1080p", "720p", null },
                enable_catalogs = true,
                enable_imdb_metadata = false,
                max_size = "inf",
                min_size = 0,
                // 2 per resolution, matching the cap set on Torrentio / Comet.
                max_streams_per_resolution = 2,
                nudity_filter = new[] { "Severe" },
                certification_filter = new[] { "Adults+" },
                language_sorting = new[]
                {
                    "English", "Tamil", "Hindi", "Malayalam", "Kannada", "Telugu", "Chinese",
                    "Russian", "Arabic", "Japanese", "Korean", "Taiwanese", "Latino", "French",
                    "Spanish", "Portuguese", "Italian", "German", "Ukrainian", "Polish", "Czech",
                    "Thai", "Indonesian", "Vietnamese", "Dutch", "Bengali", "Turkish", "Greek",
                    "Swedish", "Romanian", "Hungarian", "Finnish", "Norwegian", "Danish", "Hebrew",
                    "Lithuanian", "Punjabi", "Marathi", "Gujarati", "Bhojpuri", "Nepali", "Urdu",
                    "Tagalog", "Filipino", "Malay", "Mongolian", "Armenian", "Georgian", null,
                },
                quality_filter = new[] { "BluRay/UHD", "WEB/HD", "DVD/TV/SAT", "CAM/Screener", "Unknown" },
                hdr_filter = new[] { "HDR10", "HDR10+", "Dolby Vision", "HLG", "SDR", "Unknown" },
                live_search_streams = false,
                include_anime = true,
                enable_telegram_streams = false,
                enable_acestream_streams = false,
                max_streams = 25,
                stream_type_grouping = "separate",
                stream_type_order = new[] { "torrent", "usenet", "telegram", "http", "acestream", "youtube" },
                provider_grouping = "separate",
                stream_name_filter_mode = "disabled",
                stream_name_filter_patterns = Array.Empty<string>(),
                stream_name_filter_use_regex = false,
                torrent_sorting_priority = new[]
                {
                    new { key = "cached",     direction = "desc" },
                    new { key = "resolution", direction = "desc" },
                    new { key = "quality",    direction = "desc" },
                    new { key = "language",   direction = "desc" },
                    new { key = "size",       direction = "desc" },
                    new { key = "seeders",    direction = "desc" },
                    new { key = "created_at", direction = "desc" },
                },
                telegram_config = (object)null,
            };
            return SerializeObject(config);
        }
    }
}
