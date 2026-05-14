using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeList.Services
{
    /// <summary>
    /// Talks to MediaFusion (typically the ElfHosted instance at
    /// <c>mediafusion.elfhosted.com</c>, but the manifest URL drives
    /// the base so a self-hosted MF works the same way). MediaFusion's
    /// addon shape is the same as Torrentio's — <c>/stream/{type}/{id}.json</c>
    /// returns <c>{ streams: [...] }</c> with name/title/url/infoHash/fileIdx —
    /// so the parser reuses the regexes and quality/seeder/size detection
    /// helpers that already live on <see cref="TorrentioService"/>. The
    /// reason this is a separate service rather than a second URL inside
    /// TorrentioService is that the auth model is different (encrypted
    /// per-user URL vs flat <c>realdebrid={key}</c> segment) and the
    /// instances are configured independently by the user.
    /// </summary>
    public class MediaFusionService : IMediaFusionService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _cache;
        private readonly IConfigStore _configStore;
        private readonly ILogger<MediaFusionService> _logger;

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        // Same detection rules as TorrentioService — MediaFusion's
        // descriptions follow the de-facto Stremio convention (💾 size,
        // 👤 seeders, flag emojis for language, "1080p"/"720p" in the
        // title). Re-declared here rather than reaching into Torrentio's
        // privates so the two services stay independent.
        private static readonly (string Needle, string Quality)[] QualityNeedles =
        [
            ("2160p", "2160p"),
            ("4k",    "2160p"),
            ("1440p", "1440p"),
            ("1080p", "1080p"),
            ("720p",  "720p"),
            ("480p",  "480p"),
        ];
        private static readonly string[] AllowedQualities = ["2160p", "1440p", "1080p", "720p"];
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

        public MediaFusionService(
            IHttpClientFactory clientFactory,
            IMemoryCache cache,
            IConfigStore configStore,
            ILogger<MediaFusionService> logger)
        {
            _clientFactory = clientFactory;
            _cache = cache;
            _configStore = configStore;
            _logger = logger;
        }

        public async Task<IReadOnlyList<TorrentioStream>> GetStreamsAsync(
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
            if (addonRoot == null)
            {
                _logger.LogWarning("MediaFusion manifest URL doesn't look like a valid addon URL.");
                return [];
            }

            var stremioId = BuildStremioId(links, season, episode);
            if (stremioId == null)
            {
                return [];
            }
            var idType = stremioId.Value.Type;
            var idPath = stremioId.Value.Path;

            // Cache key fingerprints the manifest URL (which embeds the
            // encrypted user config). Hashing keeps the encrypted blob
            // out of the cache namespace dump while still partitioning
            // by user — two different MF installs on the same machine
            // get independent caches.
            var keyFingerprint = ShortFingerprint(manifestUrl);
            var cacheKey = $"mediafusion:{keyFingerprint}:{idPath}";

            // Pull the current bad-hash set so a hash marked
            // unplayable through Torrentio's resolve-stream path
            // also drops from MediaFusion's output (same SHA-1
            // identifies the same torrent regardless of which
            // addon surfaced it). Reads from the same persisted
            // bad_hashes table TorrentioService writes to.
            var badHashes = await LoadBadHashesAsync(ct);

            if (_cache.TryGetValue<IReadOnlyList<TorrentioStream>>(cacheKey, out var hit) && hit != null)
            {
                return ApplyBadHashFilter(hit, badHashes, idPath);
            }

            var url = $"{addonRoot}/stream/{idType}/{idPath}.json";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(12));

                var client = _clientFactory.CreateClient();
                var response = await client.GetAsync(url, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MediaFusion non-success {Status} for {Path}.",
                        (int)response.StatusCode, idPath);
                    return [];
                }

                var content = await response.Content.ReadAsStringAsync(cts.Token);
                var parsed = ParseStreams(content);

                // Cache the parsed (pre-bad-hash-filter) list so
                // newly-marked hashes can be filtered out without
                // waiting for the 10-minute cache to expire. Matches
                // the pattern in TorrentioService.
                _cache.Set(cacheKey, parsed, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CacheTtl,
                });
                return ApplyBadHashFilter(parsed, badHashes, idPath);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("MediaFusion call timed out ({Path}).", idPath);
                return [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MediaFusion call failed ({Path}).", idPath);
                return [];
            }
        }

        /// <summary>
        /// Pulls the addon root out of a user-pasted manifest URL.
        /// Tolerates trailing whitespace, trailing slashes, and the
        /// presence/absence of <c>/manifest.json</c>. Returns null
        /// when the input isn't a parseable absolute URL.
        /// </summary>
        private static string ExtractAddonRoot(string manifestUrl)
        {
            var trimmed = manifestUrl?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _)) return null;

            // Drop /manifest.json (with or without a trailing slash)
            // and any other trailing slash; leave query-strings alone
            // because MF doesn't use any on the manifest URL.
            const string manifestSuffix = "/manifest.json";
            if (trimmed.EndsWith(manifestSuffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^manifestSuffix.Length];
            }
            return trimmed.TrimEnd('/');
        }

        /// <summary>
        /// Same Stremio id construction TorrentioService uses — kept
        /// local rather than promoted to a shared helper because the
        /// two services are otherwise independent and bringing in the
        /// other's internals would couple them.
        /// </summary>
        private static (string Type, string Path)? BuildStremioId(AnimeSourceLinks links, int? season, int? episode)
        {
            if (episode.HasValue && episode.Value > 0)
            {
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

        private List<TorrentioStream> ParseStreams(string json)
        {
            var raw = new List<TorrentioStream>();
            if (string.IsNullOrWhiteSpace(json)) return raw;

            dynamic root;
            try { root = DeserializeObject<dynamic>(json); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MediaFusion response JSON parse failed.");
                return raw;
            }
            if (root?.streams == null) return raw;

            foreach (var s in root.streams)
            {
                string url = null;
                try { url = (string)s.url; } catch { }
                if (string.IsNullOrWhiteSpace(url)) continue;

                var name = SafeString(s.name) ?? string.Empty;
                var description = SafeString(s.description) ?? SafeString(s.title) ?? string.Empty;
                // MediaFusion puts the human-readable details in
                // `description` rather than `title` (Torrentio uses
                // `title`). Combining both fields keeps the regex
                // detection robust to either upstream convention.
                var combined = $"{name}\n{description}";

                var quality = DetectQuality(combined);
                if (quality == null || Array.IndexOf(AllowedQualities, quality) < 0)
                    continue;

                var size = DetectSize(combined);
                var seeders = DetectSeeders(combined);
                var language = DetectLanguage(combined);
                var playable = IsBrowserPlayable(url);

                string bingeGroup = null;
                try { bingeGroup = (string)s.behaviorHints?.bingeGroup; }
                catch { }

                string infoHash = null;
                int? fileIdx = null;
                try { infoHash = ((string)s.infoHash)?.ToLowerInvariant(); }
                catch { }
                try
                {
                    var fi = s.fileIdx;
                    if (fi != null) fileIdx = (int)fi;
                }
                catch { }

                raw.Add(new TorrentioStream(
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
                    Provider: "MediaFusion"));
            }

            return RankAndCap(raw);
        }

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

        private static bool IsBrowserPlayable(string url)
        {
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

        private static string SafeString(dynamic v)
        {
            try { return (string)v; }
            catch { return null; }
        }

        /// <summary>
        /// Pulls the live bad-hash set from the config store. Unlike
        /// <see cref="TorrentioService"/> we don't keep an in-memory
        /// snapshot here — MediaFusion calls are gated behind the
        /// 10-minute response cache, so the DB read happens at most
        /// every 10 minutes per unique (user, anime, episode) tuple.
        /// Fails open: a store error returns an empty set so a SQLite
        /// blip doesn't blank the source picker.
        /// </summary>
        private async Task<HashSet<string>> LoadBadHashesAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var live = await _configStore.GetActiveBadHashesAsync(DateTime.UtcNow);
                return new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load bad-hash list — proceeding unfiltered.");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private List<TorrentioStream> ApplyBadHashFilter(
            IReadOnlyList<TorrentioStream> streams, HashSet<string> badSet, string idPath)
        {
            if (badSet.Count == 0) return new List<TorrentioStream>(streams);
            var before = streams.Count;
            var filtered = streams
                .Where(s => string.IsNullOrEmpty(s.InfoHash) || !badSet.Contains(s.InfoHash))
                .ToList();
            if (filtered.Count < before)
            {
                _logger.LogInformation(
                    "Bad-hash filter dropped {Dropped}/{Total} MediaFusion streams for {Path}.",
                    before - filtered.Count, before, idPath);
            }
            return filtered;
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
