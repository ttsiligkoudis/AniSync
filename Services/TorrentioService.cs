using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeList.Services
{
    /// <summary>
    /// Talks to <c>https://torrentio.strem.fun</c>. Torrentio is the public
    /// Stremio addon that drives the de-facto "Torrentio + Real-Debrid"
    /// workflow most Stremio users know — search across torrent trackers,
    /// resolve cached files through the user's debrid provider, and emit
    /// playable direct URLs. AniSync forwards the user's RD API key through
    /// the URL config segment (<c>/realdebrid={key}/stream/...</c>).
    ///
    /// v1 surfaces RD-cached streams only — entries whose <c>url</c> is
    /// populated (the marker for "RD has this cached, here's the direct
    /// file URL"). Entries with only <c>infoHash</c> would require an
    /// extra "request caching" round-trip; deferred as a v1+1 follow-up.
    /// </summary>
    public class TorrentioService : ITorrentioService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<TorrentioService> _logger;

        private const string BaseUrl = "https://torrentio.strem.fun";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        // Quality detection is best-effort: Torrentio's `name` typically reads
        // "[RD+] Torrentio\n1080p" / "[RD+] Torrentio\n4k | HDR", so a simple
        // contains-check in priority order is enough. 2160p / 4k folded together
        // because both labels appear in the wild and mean the same thing.
        private static readonly (string Needle, string Quality)[] QualityNeedles =
        [
            ("2160p", "2160p"),
            ("4k",    "2160p"),
            ("1440p", "1440p"),
            ("1080p", "1080p"),
            ("720p",  "720p"),
            ("480p",  "480p"),
        ];

        // Torrentio embeds the file size as a 💾 token followed by a number +
        // unit (GB / MB). One regex pulls it out of either `name` or `title`.
        private static readonly Regex SizeRegex = new(@"💾\s*([\d.,]+)\s*(GB|MB|KB|TB)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Browser-playable extensions for the inline <video> path on the web
        // modal. MKV is technically possible in Chrome but unreliable enough
        // that we route it through the "open externally" affordance instead.
        private static readonly HashSet<string> PlayableExts =
            new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm", ".m4v" };

        public TorrentioService(
            IHttpClientFactory clientFactory,
            IMemoryCache cache,
            ILogger<TorrentioService> logger)
        {
            _clientFactory = clientFactory;
            _cache = cache;
            _logger = logger;
        }

        public async Task<IReadOnlyList<TorrentioStream>> GetStreamsAsync(
            string apiKey,
            AnimeSourceLinks links,
            int? season,
            int? episode,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || links == null)
            {
                return [];
            }

            var stremioId = BuildStremioId(links, season, episode);
            if (stremioId == null)
            {
                return [];
            }

            // Hashed apiKey in the cache key so the in-memory dictionary
            // doesn't carry plaintext RD tokens around — a memory dump or
            // accidental log of the cache state stays sanitised.
            var keyFingerprint = ShortFingerprint(apiKey);
            var cacheKey = $"torrentio:{keyFingerprint}:{stremioId.Path}";
            if (_cache.TryGetValue<IReadOnlyList<TorrentioStream>>(cacheKey, out var hit) && hit != null)
            {
                return hit;
            }

            var url = $"{BaseUrl}/realdebrid={Uri.EscapeDataString(apiKey)}/stream/{stremioId.Type}/{stremioId.Path}.json";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));

                var client = _clientFactory.CreateClient();
                var response = await client.GetAsync(url, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 401)
                    {
                        _logger.LogWarning("Torrentio rejected the RD API key (fingerprint={Fp}).", keyFingerprint);
                    }
                    else
                    {
                        _logger.LogWarning("Torrentio non-success {Status} for {Path}.",
                            (int)response.StatusCode, stremioId.Path);
                    }
                    return [];
                }

                var content = await response.Content.ReadAsStringAsync(cts.Token);
                var parsed = ParseStreams(content);

                // Cache even empty results — repeated clicks on the same
                // episode shouldn't hammer Torrentio when no streams exist
                // (rare ids, brand-new episodes).
                _cache.Set(cacheKey, parsed, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CacheTtl,
                });
                return parsed;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Torrentio call timed out ({Path}).", stremioId.Path);
                return [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Torrentio call failed ({Path}).", stremioId.Path);
                return [];
            }
        }

        /// <summary>
        /// Constructs the Stremio id segment Torrentio understands.
        /// Priority: IMDb (best coverage, Cinemeta-aligned) → Kitsu (anime
        /// catalog variant). Returns null when neither id is available or
        /// season/episode info is missing for a series-shaped lookup.
        /// </summary>
        private static (string Type, string Path)? BuildStremioId(AnimeSourceLinks links, int? season, int? episode)
        {
            // Episode-shaped lookup (series).
            if (episode.HasValue && episode.Value > 0)
            {
                var s = season ?? 1;
                if (!string.IsNullOrEmpty(links.ImdbId) && links.ImdbId.StartsWith("tt"))
                {
                    return ("series", $"{links.ImdbId}:{s}:{episode.Value}");
                }
                if (links.KitsuId.HasValue)
                {
                    // Kitsu ids are per-cour so no season segment.
                    return ("series", $"kitsu:{links.KitsuId.Value}:{episode.Value}");
                }
                return null;
            }

            // Movie-shaped lookup (no episode).
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

        /// <summary>
        /// Parses Torrentio's <c>{ streams: [...] }</c> response into our
        /// slim projection. Drops entries without a <c>url</c> field (those
        /// need RD to cache on demand — v1+1) and entries whose name/title
        /// doesn't carry an RD marker.
        /// </summary>
        private static List<TorrentioStream> ParseStreams(string json)
        {
            var result = new List<TorrentioStream>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            dynamic root = DeserializeObject<dynamic>(json);
            if (root?.streams == null) return result;

            foreach (var s in root.streams)
            {
                var url = (string)s.url;
                if (string.IsNullOrWhiteSpace(url)) continue;

                var name = (string)s.name ?? string.Empty;
                var title = (string)s.title ?? string.Empty;
                var combined = $"{name}\n{title}";

                var quality = DetectQuality(combined);
                var size = DetectSize(combined);
                var playable = IsBrowserPlayable(url);

                string bingeGroup = null;
                try { bingeGroup = (string)s.behaviorHints?.bingeGroup; }
                catch { /* missing object — leave null */ }

                result.Add(new TorrentioStream(
                    Name: name,
                    Title: title,
                    Url: url,
                    Quality: quality,
                    Size: size,
                    Playable: playable,
                    BingeGroup: bingeGroup));
            }
            return result;
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
            // Normalise "1,234" → "1234" for the display string; keep the
            // unit casing the user expects (GB / MB / …).
            var value = m.Groups[1].Value.Replace(",", "");
            return $"{value} {m.Groups[2].Value.ToUpperInvariant()}";
        }

        private static bool IsBrowserPlayable(string url)
        {
            // Real-Debrid URLs sometimes carry the original filename in the
            // last segment, sometimes a download token — strip query, take
            // the last path segment, check its extension. Unknown / missing
            // extension defaults to playable so the user at least gets a
            // try-to-play attempt; the <video> tag will surface its own
            // error if it can't decode.
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

        private static string ShortFingerprint(string apiKey)
        {
            // First 8 hex chars of SHA-256 — enough to distinguish keys in
            // the cache namespace, not enough to reverse.
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(apiKey));
            var sb = new StringBuilder(8);
            for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
