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

        // Display order for the filtered+ranked output. Higher resolutions
        // first so the user's eye lands on the best available quality.
        // Anything not listed here is dropped (480p, SD, unknown).
        private static readonly string[] AllowedQualities = ["2160p", "1440p", "1080p", "720p"];
        private const int PerQualityCap = 2;

        // Torrentio embeds the file size as a 💾 token followed by a number +
        // unit (GB / MB). One regex pulls it out of either `name` or `title`.
        private static readonly Regex SizeRegex = new(@"💾\s*([\d.,]+)\s*(GB|MB|KB|TB)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Seeder count Torrentio prefixes with 👤 — drives the per-quality
        // popularity rank. Missing token reads as 0 seeders (those entries
        // sort to the bottom of their bucket and usually fall outside the
        // top-N cap, which is the behaviour we want).
        private static readonly Regex SeedersRegex = new(@"👤\s*(\d+)",
            RegexOptions.Compiled);

        // Country-flag emojis are pairs of regional-indicator codepoints
        // (U+1F1E6..U+1F1FF). Torrentio emits them when language metadata
        // is in the release name — usually as the rightmost token in the
        // title. We grab every run of 1+ flags and join with " / " for the
        // multi-audio case (🇯🇵🇺🇸 → "🇯🇵 / 🇺🇸").
        private static readonly Regex FlagRegex = new(
            @"(?:\uD83C[\uDDE6-\uDDFF]){2}",
            RegexOptions.Compiled);

        // Fallback tokens when no flag emoji is present. Order matters: a
        // "MULTi" release is also "DUAL" sometimes — we pick the more
        // specific label that appears first.
        private static readonly (string Needle, string Label)[] LanguageTokens =
        [
            ("MULTi",       "Multi"),
            ("Multi Audio", "Multi"),
            ("DUAL",        "Dual"),
            ("Dual Audio",  "Dual"),
            ("DUBBED",      "Dub"),
            ("SUBBED",      "Sub"),
        ];

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
            var idType = stremioId.Value.Type;
            var idPath = stremioId.Value.Path;

            // Hashed apiKey in the cache key so the in-memory dictionary
            // doesn't carry plaintext RD tokens around — a memory dump or
            // accidental log of the cache state stays sanitised.
            var keyFingerprint = ShortFingerprint(apiKey);
            var cacheKey = $"torrentio:{keyFingerprint}:{idPath}";
            if (_cache.TryGetValue<IReadOnlyList<TorrentioStream>>(cacheKey, out var hit) && hit != null)
            {
                return hit;
            }

            var url = $"{BaseUrl}/realdebrid={Uri.EscapeDataString(apiKey)}/stream/{idType}/{idPath}.json";

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
                            (int)response.StatusCode, idPath);
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
                _logger.LogWarning("Torrentio call timed out ({Path}).", idPath);
                return [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Torrentio call failed ({Path}).", idPath);
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
                // For IMDb series ids, the mapping's ImdbSeason wins over
                // whatever the URL passed: the URL season is the AniSync
                // cour-internal number (almost always 1 because each cour
                // is a self-contained anime entry in AniSync), but
                // Torrentio addresses the franchise's IMDb listing with
                // the franchise-wide season number. Fall back to the URL
                // value only when the mapping has no opinion.
                var s = links.ImdbSeason ?? season ?? 1;
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
        /// Parses Torrentio's <c>{ streams: [...] }</c> response, then
        /// filters + ranks: drop entries without a <c>url</c> (need RD
        /// to cache on demand — v1+1), drop entries below 720p (480p
        /// and below are too low to be worth surfacing for anime), and
        /// keep only the top <see cref="PerQualityCap"/> entries per
        /// resolution sorted by seeder count desc. Final order is
        /// 2160p → 1440p → 1080p → 720p with the two most-seeded
        /// releases inside each bucket.
        /// </summary>
        private static List<TorrentioStream> ParseStreams(string json)
        {
            var raw = new List<TorrentioStream>();
            if (string.IsNullOrWhiteSpace(json)) return raw;

            dynamic root = DeserializeObject<dynamic>(json);
            if (root?.streams == null) return raw;

            foreach (var s in root.streams)
            {
                var url = (string)s.url;
                if (string.IsNullOrWhiteSpace(url)) continue;

                var name = (string)s.name ?? string.Empty;
                var title = (string)s.title ?? string.Empty;
                var combined = $"{name}\n{title}";

                var quality = DetectQuality(combined);
                // Quality must be detectable AND ≥720p — otherwise we
                // don't have a confident enough signal to rank the entry,
                // and the user asked for high-res only.
                if (quality == null || Array.IndexOf(AllowedQualities, quality) < 0)
                    continue;

                var size = DetectSize(combined);
                var seeders = DetectSeeders(combined);
                var language = DetectLanguage(combined);
                var playable = IsBrowserPlayable(url);

                string bingeGroup = null;
                try { bingeGroup = (string)s.behaviorHints?.bingeGroup; }
                catch { /* missing object — leave null */ }

                raw.Add(new TorrentioStream(
                    Name: name,
                    Title: title,
                    Url: url,
                    Quality: quality,
                    Size: size,
                    Playable: playable,
                    BingeGroup: bingeGroup,
                    Seeders: seeders,
                    Language: language));
            }

            return RankAndCap(raw);
        }

        /// <summary>
        /// Group by quality, sort each group by seeder count desc,
        /// take the top <see cref="PerQualityCap"/>, then re-emit in
        /// <see cref="AllowedQualities"/> order. Tie-break by larger
        /// file size — for a given seed count, the bigger release is
        /// usually the higher-bitrate one (better source / less
        /// compression). Stable enough that re-renders look the same.
        /// </summary>
        private static List<TorrentioStream> RankAndCap(List<TorrentioStream> raw)
        {
            var byQuality = raw
                .GroupBy(s => s.Quality)
                .ToDictionary(g => g.Key, g => g
                    .OrderByDescending(s => s.Seeders)
                    .ThenByDescending(s => ParseSizeBytes(s.Size))
                    .Take(PerQualityCap)
                    .ToList());

            var ranked = new List<TorrentioStream>(AllowedQualities.Length * PerQualityCap);
            foreach (var q in AllowedQualities)
            {
                if (byQuality.TryGetValue(q, out var bucket))
                {
                    ranked.AddRange(bucket);
                }
            }
            return ranked;
        }

        /// <summary>
        /// Best-effort bytes from a "12.4 GB" / "850 MB" display string.
        /// Only used as a tie-breaker inside the seeder-count sort, so
        /// a parse miss returning 0 is harmless — those entries just
        /// settle into stable insertion order behind anything we did
        /// manage to size.
        /// </summary>
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
            // Normalise "1,234" → "1234" for the display string; keep the
            // unit casing the user expects (GB / MB / …).
            var value = m.Groups[1].Value.Replace(",", "");
            return $"{value} {m.Groups[2].Value.ToUpperInvariant()}";
        }

        private static int DetectSeeders(string haystack)
        {
            var m = SeedersRegex.Match(haystack);
            if (!m.Success) return 0;
            return int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
        }

        /// <summary>
        /// Pulls language hints out of Torrentio's title. Priority: flag
        /// emojis (every flag found, joined with " / ") → MULTi/DUAL/etc
        /// token fallback. Returns null when neither is present — most
        /// raw fansub releases have no language metadata and surfacing
        /// "Unknown" everywhere would just be noise.
        /// </summary>
        private static string DetectLanguage(string haystack)
        {
            var flagMatches = FlagRegex.Matches(haystack);
            if (flagMatches.Count > 0)
            {
                // Dedupe — some titles repeat the same flag in two places
                // (e.g. once as banner art, once as track summary).
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
