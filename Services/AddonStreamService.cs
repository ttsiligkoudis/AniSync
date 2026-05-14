using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeList.Services
{
    /// <summary>
    /// Talks to any Stremio-compatible stream addon — Torrentio,
    /// MediaFusion, Comet, Jackettio, AIOStreams, anything else that
    /// follows the addon protocol. The user pastes a manifest URL on
    /// the Configure page and we GET it once to derive the display
    /// name; per-episode lookups strip <c>/manifest.json</c>, append
    /// <c>/stream/{type}/{id}.json</c>, and parse the resulting
    /// <c>{ streams: [...] }</c> body. Addon-specific config segments
    /// (Torrentio's <c>realdebrid=KEY</c>, MediaFusion's encrypted
    /// blob, Comet's provider toggles) flow through transparently in
    /// the URL — we never crack them open.
    /// </summary>
    public class AddonStreamService : IAddonStreamService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<AddonStreamService> _logger;

        private static readonly TimeSpan StreamsCacheTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan StreamsTimeout = TimeSpan.FromSeconds(12);

        // Quality detection follows the de-facto Torrentio convention
        // every well-known stream addon adopted: "1080p" / "4k" / "720p"
        // tokens in the name or description.
        private static readonly (string Needle, string Quality)[] QualityNeedles =
        [
            ("2160p", "2160p"),
            ("4k",    "2160p"),
            ("1440p", "1440p"),
            ("1080p", "1080p"),
            ("720p",  "720p"),
            ("480p",  "480p"),
        ];

        // Anything below 720p is dropped — anime users almost always
        // want at least 720p, and surfacing 480p just dilutes the picker.
        private static readonly string[] AllowedQualities = ["2160p", "1440p", "1080p", "720p"];
        // Per-quality cap applies *per addon* (so a 3-addon setup with
        // 5/quality each surfaces up to 60 entries across 4 buckets).
        // The merge step in AnimeController re-applies the cap after
        // dedupe so the rendered list stays manageable regardless.
        private const int PerQualityCap = 5;

        private static readonly Regex SizeRegex = new(@"💾\s*([\d.,]+)\s*(GB|MB|KB|TB)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SeedersRegex = new(@"👤\s*(\d+)",
            RegexOptions.Compiled);
        private static readonly Regex FlagRegex = new(
            @"(?:\uD83C[\uDDE6-\uDDFF]){2}",
            RegexOptions.Compiled);
        private static readonly (string Needle, string Label)[] LanguageTokens =
        [
            ("MULTi",       "Multi"),
            ("Multi Audio", "Multi"),
            ("DUAL",        "Dual"),
            ("Dual Audio",  "Dual"),
            ("DUBBED",      "Dub"),
            ("SUBBED",      "Sub"),
        ];
        private static readonly HashSet<string> PlayableExts =
            new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm", ".m4v" };

        // Codec / container tokens that the in-browser <video> element
        // doesn't decode (Chrome / Firefox on desktop and Android). Anime
        // releases are overwhelmingly H.265/HEVC or AV1 inside MKV, and
        // attempting inline playback for those just burns ArtPlayer's
        // reconnect budget on five guaranteed-to-fail loads before the
        // codec-not-supported message lands. Matching any of these tokens
        // in the stream's name+description routes the row straight to the
        // "Open externally" affordance instead. Word-boundary anchored so
        // we don't false-match "shevc" or "av1d" or similar.
        private static readonly Regex NonBrowserCodecRegex = new(
            @"\b(?:hevc|x265|h\.?265|av1|mkv|matroska|10[\s-]?bit)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public AddonStreamService(
            IHttpClientFactory clientFactory,
            IMemoryCache cache,
            ILogger<AddonStreamService> logger)
        {
            _clientFactory = clientFactory;
            _cache = cache;
            _logger = logger;
        }

        public async Task<StreamAddon> FetchManifestAsync(string manifestUrl, CancellationToken ct = default)
        {
            var trimmed = manifestUrl?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != "http" && parsed.Scheme != "https"))
                return null;
            if (!trimmed.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ManifestTimeout);

                var client = _clientFactory.CreateClient();
                var response = await client.GetAsync(trimmed, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Addon manifest fetch returned {Status} for {Url}.",
                        (int)response.StatusCode, trimmed);
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(cts.Token);
                JObject manifest;
                try { manifest = JObject.Parse(body); }
                catch
                {
                    _logger.LogInformation("Addon manifest at {Url} did not parse as JSON.", trimmed);
                    return null;
                }

                // Validate: the addon must advertise "stream" as one of
                // its resources. Otherwise it's a catalog-only addon
                // and won't return anything from /stream queries.
                var resources = manifest["resources"];
                if (!HasStreamResource(resources))
                {
                    _logger.LogInformation("Addon at {Url} doesn't advertise stream support.", trimmed);
                    return null;
                }

                var name = (string)manifest["name"];
                if (string.IsNullOrWhiteSpace(name))
                {
                    // Fall back to host segment so the user has *some*
                    // label rather than an empty pill.
                    name = parsed.Host;
                }

                return new StreamAddon { Url = trimmed, Name = name.Trim() };
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Addon manifest fetch timed out for {Url}.", trimmed);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Addon manifest fetch failed for {Url}.", trimmed);
                return null;
            }
        }

        /// <summary>
        /// Stremio's <c>resources</c> field can be either a flat array
        /// of strings (<c>["stream", "catalog"]</c>) or an array of
        /// objects (<c>[{ "name": "stream", "types": [...] }, ...]</c>).
        /// We accept either as long as "stream" appears somewhere.
        /// </summary>
        private static bool HasStreamResource(JToken resources)
        {
            if (resources is not JArray arr) return false;
            foreach (var entry in arr)
            {
                if (entry.Type == JTokenType.String
                    && string.Equals((string)entry, "stream", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (entry is JObject obj
                    && string.Equals((string)obj["name"], "stream", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public async Task<IReadOnlyList<AddonStream>> GetStreamsAsync(
            string manifestUrl,
            AnimeSourceLinks links,
            int? season,
            int? episode,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl) || links == null)
            {
                return [];
            }

            var addonRoot = ExtractAddonRoot(manifestUrl);
            if (addonRoot == null) return [];

            var stremioId = BuildStremioId(links, season, episode);
            if (stremioId == null) return [];
            var idType = stremioId.Value.Type;
            var idPath = stremioId.Value.Path;

            // Fingerprint the manifest URL so addon-specific encrypted
            // config segments don't leak into the in-process cache
            // namespace (memory dumps stay sanitised).
            var keyFingerprint = ShortFingerprint(manifestUrl);
            var cacheKey = $"addon:{keyFingerprint}:{idPath}";

            if (_cache.TryGetValue<IReadOnlyList<AddonStream>>(cacheKey, out var hit) && hit != null)
            {
                return hit;
            }

            var streamsUrl = $"{addonRoot}/stream/{idType}/{idPath}.json";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(StreamsTimeout);

                var client = _clientFactory.CreateClient();
                var response = await client.GetAsync(streamsUrl, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Addon stream fetch {Status} for {Path} via {Host}.",
                        (int)response.StatusCode, idPath, addonRoot);
                    return [];
                }

                var content = await response.Content.ReadAsStringAsync(cts.Token);
                var parsed = ParseStreams(content, addonRoot);

                _cache.Set(cacheKey, parsed, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = StreamsCacheTtl,
                });
                return parsed;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Addon stream fetch timed out ({Path} via {Host}).", idPath, addonRoot);
                return [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Addon stream fetch failed ({Path} via {Host}).", idPath, addonRoot);
                return [];
            }
        }

        /// <summary>
        /// Strips <c>/manifest.json</c> and any trailing slash from the
        /// user-pasted URL to derive the addon root that prefixes
        /// <c>/stream/{type}/{id}.json</c>. Returns null when the input
        /// isn't a valid absolute URL.
        /// </summary>
        private static string ExtractAddonRoot(string manifestUrl)
        {
            var trimmed = manifestUrl?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _)) return null;

            const string manifestSuffix = "/manifest.json";
            if (trimmed.EndsWith(manifestSuffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^manifestSuffix.Length];
            }
            return trimmed.TrimEnd('/');
        }

        /// <summary>
        /// Builds the Stremio id segment for the addon's /stream path.
        /// Priority: IMDb (best coverage, Cinemeta-aligned) → Kitsu
        /// (anime catalog variant). Returns null when neither id is
        /// available, or when an episode lookup is requested without
        /// season/episode info.
        /// </summary>
        private static (string Type, string Path)? BuildStremioId(AnimeSourceLinks links, int? season, int? episode)
        {
            if (episode.HasValue && episode.Value > 0)
            {
                // For IMDb series ids the mapping's ImdbSeason wins
                // over the AniSync-internal season number — that's
                // almost always 1 because each AniSync cour is its own
                // entry, while IMDb addresses the franchise season-wide.
                var s = links.ImdbSeason ?? season ?? 1;
                if (!string.IsNullOrEmpty(links.ImdbId) && links.ImdbId.StartsWith("tt"))
                {
                    return ("series", $"{links.ImdbId}:{s}:{episode.Value}");
                }
                if (links.KitsuId.HasValue)
                {
                    return ("series", $"kitsu:{links.KitsuId.Value}:{episode.Value}");
                }
                return null;
            }

            if (!string.IsNullOrEmpty(links.ImdbId) && links.ImdbId.StartsWith("tt"))
            {
                return ("movie", links.ImdbId);
            }
            if (links.KitsuId.HasValue)
            {
                return ("movie", $"kitsu:{links.KitsuId.Value}");
            }
            return null;
        }

        private List<AddonStream> ParseStreams(string json, string addonRoot)
        {
            var raw = new List<AddonStream>();
            if (string.IsNullOrWhiteSpace(json)) return raw;

            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Addon stream response did not parse as JSON ({Host}).", addonRoot);
                return raw;
            }
            if (root["streams"] is not JArray streams) return raw;

            // Per-addon display label — the addon's manifest name lives
            // on the StreamAddon entry stored in IConfigStore, not on
            // each stream response, so we derive a fallback label from
            // the URL host here. The merge step in AnimeController
            // overrides this with the persisted display name when it
            // has one.
            string providerFallback;
            try { providerFallback = new Uri(addonRoot).Host; } catch { providerFallback = "Addon"; }

            foreach (var s in streams.OfType<JObject>())
            {
                var url = (string)s["url"];
                if (string.IsNullOrWhiteSpace(url)) continue;

                var name = (string)s["name"] ?? string.Empty;
                // Different addons populate different descriptive
                // fields — Torrentio uses `title`, MediaFusion uses
                // `description`. Combine both to keep the regex
                // detection robust either way.
                var description = (string)s["description"] ?? (string)s["title"] ?? string.Empty;
                var combined = $"{name}\n{description}";

                var quality = DetectQuality(combined);
                if (quality == null || Array.IndexOf(AllowedQualities, quality) < 0)
                    continue;

                var size = DetectSize(combined);
                var seeders = DetectSeeders(combined);
                var language = DetectLanguage(combined);
                var playable = IsBrowserPlayable(url, combined);

                string bingeGroup = null;
                if (s["behaviorHints"] is JObject hints)
                {
                    bingeGroup = (string)hints["bingeGroup"];
                }

                var infoHash = ((string)s["infoHash"])?.ToLowerInvariant();
                int? fileIdx = null;
                if (s["fileIdx"] != null && int.TryParse((string)s["fileIdx"], out var fi))
                {
                    fileIdx = fi;
                }

                raw.Add(new AddonStream(
                    Name: name,
                    Title: description,
                    Url: url,
                    Quality: quality,
                    Size: size,
                    Playable: playable,
                    BingeGroup: bingeGroup,
                    Seeders: seeders,
                    Language: language,
                    InfoHash: infoHash,
                    FileIdx: fileIdx,
                    Provider: providerFallback));
            }

            return RankAndCap(raw);
        }

        private static List<AddonStream> RankAndCap(List<AddonStream> raw)
        {
            var byQuality = raw
                .GroupBy(s => s.Quality)
                .ToDictionary(g => g.Key, g => g
                    .OrderByDescending(s => s.Seeders)
                    .ThenByDescending(s => ParseSizeBytes(s.Size))
                    .Take(PerQualityCap)
                    .ToList());

            var ranked = new List<AddonStream>(AllowedQualities.Length * PerQualityCap);
            foreach (var q in AllowedQualities)
            {
                if (byQuality.TryGetValue(q, out var bucket))
                {
                    ranked.AddRange(bucket);
                }
            }
            return ranked;
        }

        private static long ParseSizeBytes(string size)
        {
            if (string.IsNullOrEmpty(size)) return 0;
            var parts = size.Split(' ');
            if (parts.Length != 2) return 0;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
                return 0;
            return parts[1].ToUpperInvariant() switch
            {
                "TB" => (long)(v * 1024L * 1024L * 1024L * 1024L),
                "GB" => (long)(v * 1024L * 1024L * 1024L),
                "MB" => (long)(v * 1024L * 1024L),
                "KB" => (long)(v * 1024L),
                _    => 0,
            };
        }

        private static string DetectQuality(string haystack)
        {
            foreach (var (needle, label) in QualityNeedles)
            {
                if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return label;
            }
            return null;
        }

        private static string DetectSize(string haystack)
        {
            var m = SizeRegex.Match(haystack);
            if (!m.Success) return null;
            var value = m.Groups[1].Value.Replace(",", "");
            return $"{value} {m.Groups[2].Value.ToUpperInvariant()}";
        }

        private static int DetectSeeders(string haystack)
        {
            var m = SeedersRegex.Match(haystack);
            if (!m.Success) return 0;
            return int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
        }

        private static string DetectLanguage(string haystack)
        {
            var flagMatches = FlagRegex.Matches(haystack);
            if (flagMatches.Count > 0)
            {
                var seen = new HashSet<string>();
                var ordered = new List<string>();
                foreach (Match m in flagMatches)
                {
                    if (seen.Add(m.Value)) ordered.Add(m.Value);
                }
                return string.Join(" / ", ordered);
            }
            foreach (var (needle, label) in LanguageTokens)
            {
                if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return label;
            }
            return null;
        }

        private static bool IsBrowserPlayable(string url, string nameAndDescription)
        {
            // First check the textual hints. Most debrid-CDN URLs are
            // token-paths with no extension, so we can't rely on the
            // URL alone — but the addon's name/title routinely carries
            // codec tokens (Torrentio puts them in the name line under
            // the quality, MediaFusion in the description). Catching
            // HEVC / AV1 / MKV here saves us five guaranteed-to-fail
            // reconnect cycles in ArtPlayer before the codec error
            // surfaces.
            if (!string.IsNullOrEmpty(nameAndDescription)
                && NonBrowserCodecRegex.IsMatch(nameAndDescription))
                return false;

            try
            {
                var uri = new Uri(url);
                var last = uri.AbsolutePath.Split('/').LastOrDefault();
                if (string.IsNullOrEmpty(last)) return true;
                var dot = last.LastIndexOf('.');
                if (dot < 0) return true;
                var ext = last[dot..];
                return PlayableExts.Contains(ext);
            }
            catch
            {
                return true;
            }
        }

        private static string ShortFingerprint(string s)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder(8);
            for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
