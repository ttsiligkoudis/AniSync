using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeList.Services
{
    /// <summary>
    /// Jimaku (https://jimaku.cc) subtitle lookup. Two-step protocol:
    /// search entries by AniList id, then fetch the file list for the
    /// best-matching entry. File entries are filtered to the requested
    /// episode by scanning their filename — Jimaku doesn't enforce a
    /// schema on uploads so episode numbers live in the filename
    /// (commonly <c>"[Group] Show - 03 (1080p).en.srt"</c> or
    /// <c>"Show.S01E03.srt"</c>). Language is determined by sniffing
    /// a few KB of the actual subtitle bytes — filename-only heuristics
    /// misfire on Jimaku because uploaders rarely tag language in the
    /// name; an <c>[Erai-raws]</c>-style filename can be JP closed
    /// captions just as easily as English fansubs.
    /// </summary>
    public class JimakuSubtitlesService : IJimakuSubtitlesService
    {
        private const string ApiBase = "https://jimaku.cc/api";

        private static readonly TimeSpan ListCacheTtl = TimeSpan.FromHours(2);
        private static readonly TimeSpan LangCacheTtl = TimeSpan.FromDays(7);
        private static readonly TimeSpan NoKeyLogOnceWindow = TimeSpan.FromHours(1);

        // Same shape OpenSubtitlesService uses — caps the menu length
        // for popular shows where Jimaku has many crowd uploads. First
        // variant is unlabelled, 2+ get a numeric suffix.
        private const int MaxVariantsPerLanguage = 5;

        // First N bytes pulled when sniffing language. SRT cue blocks
        // are short — 4 KB usually yields 10+ subtitle cues which is
        // plenty to tell hiragana / katakana / kanji from Latin script.
        private const int SniffByteLimit = 4096;

        // CJK ratio threshold — files with at least this many CJK
        // characters per total decoded chars are Japanese (also
        // catches Chinese / Korean — distinguished separately via
        // script-specific Unicode ranges below). 3% is conservative:
        // even English subs that quote a Japanese term land well
        // below this on a 4 KB sample.
        private const double CjkRatioThreshold = 0.03;

        // Strong filename markers — when present we trust them and
        // skip the byte sniff (saves a round trip per file). Anything
        // else falls through to the content classifier.
        private static readonly (Regex Pattern, string Code)[] StrongFilenameMarkers =
        [
            (new Regex(@"(?:^|[\.\s_\-\[\(])(?:ja|jp|jpn|japanese|日本語|字幕)(?:[\.\s_\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "jpn"),
            (new Regex(@"(?:^|[\.\s_\-\[\(])(?:en|eng|english)(?:[\.\s_\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "eng"),
            (new Regex(@"(?:^|[\.\s_\-\[\(])(?:es|spa|spanish|español)(?:[\.\s_\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "spa"),
            (new Regex(@"(?:^|[\.\s_\-\[\(])(?:pt|por|portuguese)(?:[\.\s_\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "por"),
            (new Regex(@"(?:^|[\.\s_\-\[\(])(?:fr|fre|fra|french)(?:[\.\s_\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "fre"),
            (new Regex(@"(?:^|[\.\s_\-\[\(])(?:de|ger|deu|german)(?:[\.\s_\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ger"),
            (new Regex(@"(?:^|[\.\s_\-\[\(])(?:it|ita|italian)(?:[\.\s_\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ita"),
            (new Regex(@"(?:^|[\.\s_\-\[\(])(?:zh|chi|zho|chinese|中文|繁中|简中)(?:[\.\s_\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "chi"),
            (new Regex(@"(?:^|[\.\s_\-\[\(])(?:ko|kor|korean|한국어)(?:[\.\s_\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "kor"),
        ];

        private static readonly Dictionary<string, string> LanguageLabels =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "English",
            ["jpn"] = "Japanese",
            ["spa"] = "Spanish",
            ["por"] = "Portuguese",
            ["fre"] = "French",
            ["ger"] = "German",
            ["ita"] = "Italian",
            ["chi"] = "Chinese",
            ["kor"] = "Korean",
        };

        // English first, then anything else — the user explicitly
        // wants EN as the primary fallback when OpenSubtitles misses.
        // Japanese moves toward the bottom because most users
        // selecting Jimaku tracks are doing it for English coverage.
        private static readonly string[] LanguageOrder =
            ["eng", "spa", "por", "fre", "ger", "ita", "chi", "kor", "jpn"];

        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<JimakuSubtitlesService> _logger;
        private readonly string _apiKey;

        // We don't want to spam logs every episode pick when the key is
        // unset — that's a totally valid deployment (Jimaku is optional
        // augmentation, not a hard dep). Log once per hour at most.
        private DateTime _nextNoKeyLogAt = DateTime.MinValue;

        public JimakuSubtitlesService(
            IHttpClientFactory clientFactory,
            IMemoryCache cache,
            IConfiguration configuration,
            ILogger<JimakuSubtitlesService> logger)
        {
            _clientFactory = clientFactory;
            _cache = cache;
            _logger = logger;
            // Same precedence as SqliteConfigStore: appsettings first
            // (lets devs override locally), env var as the fallback
            // (Fly.io / Docker friendly).
            _apiKey = (configuration["JIMAKU_API_KEY"]
                ?? Environment.GetEnvironmentVariable("JIMAKU_API_KEY")
                ?? string.Empty).Trim();
        }

        public async Task<IReadOnlyList<SubtitleTrack>> SearchAsync(
            int anilistId, int episode, string filename = null, CancellationToken ct = default)
        {
            if (anilistId <= 0 || episode <= 0) return [];
            if (string.IsNullOrEmpty(_apiKey))
            {
                if (DateTime.UtcNow >= _nextNoKeyLogAt)
                {
                    _nextNoKeyLogAt = DateTime.UtcNow + NoKeyLogOnceWindow;
                    _logger.LogInformation("Jimaku lookup skipped — JIMAKU_API_KEY not configured.");
                }
                return [];
            }

            // Cache key matches the OpenSubtitles shape so the two
            // providers' cache footprints look symmetric. Filename
            // doesn't actually influence the upstream query (Jimaku
            // matches by AniList id), but cached entries should still
            // partition by it because the *episode-matching regex*
            // below may pick different files when a user's filename
            // hints at a specific release group — keeping cache
            // entries narrow avoids cross-contamination.
            var normalisedFilename = !string.IsNullOrWhiteSpace(filename)
                ? filename.Trim().ToLowerInvariant()
                : null;
            var filenameKey = string.IsNullOrEmpty(normalisedFilename) ? "_" : normalisedFilename;
            var cacheKey = $"jimaku:list:{anilistId}:{episode}:{filenameKey}";
            if (_cache.TryGetValue<IReadOnlyList<SubtitleTrack>>(cacheKey, out var hit) && hit != null)
            {
                return hit;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // 12s envelope: entries fetch (~1s) + files fetch (~1s) +
                // parallel per-file language sniff (~3s) plus headroom.
                // Each sniff has its own 3s cap so a single slow file
                // can't drag the whole list past this budget.
                cts.CancelAfter(TimeSpan.FromSeconds(12));
                var client = _clientFactory.CreateClient();

                var entryIds = await SearchEntriesAsync(client, anilistId, cts.Token);
                if (entryIds.Count == 0)
                {
                    // Negative cache short — Jimaku is community-driven
                    // and entries appear retroactively. 30 min keeps us
                    // from re-checking every render but doesn't trap
                    // us on a brand-new show too long.
                    _cache.Set(cacheKey, Array.Empty<SubtitleTrack>(), new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
                    });
                    return [];
                }

                // Pull files from every matched entry — anime franchises
                // sometimes have a "season 1" entry and a "BD batch"
                // entry coexisting under the same AniList id; merging
                // both gives the user the full pick. Concurrent fetch
                // so the per-entry round-trip doesn't compound.
                var fileTasks = entryIds
                    .Select(id => FetchEntryFilesAsync(client, id, cts.Token))
                    .ToArray();
                var allFiles = (await Task.WhenAll(fileTasks)).SelectMany(x => x).ToList();

                var matched = FilterByEpisode(allFiles, episode);
                var classified = await ClassifyLanguagesAsync(matched, client, cts.Token);
                var tracks = BuildTracks(classified);

                _cache.Set(cacheKey, tracks, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ListCacheTtl,
                });
                return tracks;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jimaku search failed (anilist={Anilist} ep={Ep}).", anilistId, episode);
                return [];
            }
        }

        private async Task<List<long>> SearchEntriesAsync(HttpClient client, int anilistId, CancellationToken ct)
        {
            var url = $"{ApiBase}/entries/search?anilist_id={anilistId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", _apiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("Jimaku entries/search returned {Status} for anilist {Id}.", (int)res.StatusCode, anilistId);
                return [];
            }
            var body = await res.Content.ReadAsStringAsync(ct);
            // Response is a top-level JSON array of entry objects;
            // each has an integer `id` that keys the files endpoint.
            // Dynamic typing here matches OpenSubtitlesService's
            // ParseList — Jimaku's schema evolves and we want extra
            // fields to roll off rather than break the deserialise.
            var ids = new List<long>();
            dynamic arr = DeserializeObject<dynamic>(body);
            if (arr == null) return ids;
            foreach (var e in arr)
            {
                long? id = (long?)e.id;
                if (id.HasValue && id.Value > 0) ids.Add(id.Value);
            }
            return ids;
        }

        private async Task<List<JimakuFile>> FetchEntryFilesAsync(HttpClient client, long entryId, CancellationToken ct)
        {
            var url = $"{ApiBase}/entries/{entryId}/files";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", _apiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("Jimaku files endpoint returned {Status} for entry {Id}.", (int)res.StatusCode, entryId);
                return [];
            }
            var body = await res.Content.ReadAsStringAsync(ct);
            var files = new List<JimakuFile>();
            dynamic arr = DeserializeObject<dynamic>(body);
            if (arr == null) return files;
            foreach (var f in arr)
            {
                var name = (string)f.name;
                var fileUrl = (string)f.url;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(fileUrl)) continue;
                files.Add(new JimakuFile(name, fileUrl));
            }
            return files;
        }

        // Matches the episode number in a filename. Anime release names
        // pack the episode in a handful of common shapes — we try the
        // most specific patterns first so e.g. "[Group] Show - 03" wins
        // over a stray "1080p" that contains digits.
        private static readonly Regex[] EpisodeRegexes =
        [
            new Regex(@"\bS\d{1,2}E(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\b(?:E|EP|Episode|Ep\.)\s*(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:^|[\s\-_\.\[\(])(\d{1,3})(?=v\d)?(?:[\s\-_\.\[\)\]]|$)", RegexOptions.Compiled),
        ];

        // Substrings that mean "this is a batch/extras file, not a
        // per-episode subtitle" — drop them outright so the menu
        // doesn't carry "(NCOP)" / "(NCED)" tracks the player can't
        // do anything sensible with.
        private static readonly Regex NonEpisodeFile = new(
            @"\b(NCOP|NCED|OP\d?|ED\d?|menu|extras?|trailer|preview|pv)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static List<JimakuFile> FilterByEpisode(List<JimakuFile> files, int episode)
        {
            if (files.Count == 0) return files;
            var filtered = new List<JimakuFile>();
            foreach (var f in files)
            {
                if (NonEpisodeFile.IsMatch(f.Name)) continue;

                int? parsed = null;
                foreach (var rx in EpisodeRegexes)
                {
                    var m = rx.Match(f.Name);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
                    {
                        parsed = n;
                        break;
                    }
                }

                // If we couldn't parse a number AND the entry contains
                // only one file, accept it — the upload is probably a
                // single-episode submission. Otherwise require an
                // exact match.
                if (parsed.HasValue)
                {
                    if (parsed.Value == episode) filtered.Add(f);
                }
                else if (files.Count == 1)
                {
                    filtered.Add(f);
                }
            }
            return filtered;
        }

        /// <summary>
        /// Classifies each file's language. Strong filename markers
        /// (<c>.en.srt</c> / <c>.ja.ass</c> / etc.) short-circuit the
        /// expensive path. Everything else gets a Range-limited GET
        /// of the first few KB so we can look at the actual bytes —
        /// Jimaku uploaders rarely tag language in the filename, so
        /// without this every English fansub got mislabelled as
        /// Japanese (the previous default). Per-URL cache is 7-day:
        /// the language of a given upload never changes.
        /// </summary>
        private async Task<List<(string Code, JimakuFile File)>> ClassifyLanguagesAsync(
            List<JimakuFile> files, HttpClient client, CancellationToken ct)
        {
            if (files.Count == 0) return [];

            var tasks = files.Select(async f =>
            {
                var fromName = MarkerFromFilename(f.Name);
                if (fromName != null) return (fromName, f);

                var cacheKey = $"jimaku:lang:{f.Url}";
                if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached != null)
                {
                    return (cached, f);
                }

                var sniffed = await SniffLanguageAsync(client, f.Url, ct);
                _cache.Set(cacheKey, sniffed, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = LangCacheTtl,
                });
                return (sniffed, f);
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        private async Task<string> SniffLanguageAsync(HttpClient client, string url, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", _apiKey);
                req.Headers.TryAddWithoutValidation("Range", $"bytes=0-{SniffByteLimit - 1}");
                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    _logger.LogDebug("Jimaku language sniff non-OK {Status} for {Url}", (int)res.StatusCode, url);
                    return "eng"; // safest fallback — see comment below
                }
                var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
                if (bytes.Length == 0) return "eng";

                // SRT bodies are usually UTF-8 (with or without BOM)
                // or Shift-JIS for older Japanese uploads. Try UTF-8
                // first with replacement, then fall back to Shift-JIS
                // if the UTF-8 decode produced lots of replacement
                // chars (signals it wasn't actually UTF-8).
                string text;
                try
                {
                    text = Encoding.UTF8.GetString(bytes);
                    if (text.Count(c => c == '�') > 20)
                    {
                        text = TryDecodeShiftJis(bytes) ?? text;
                    }
                }
                catch
                {
                    text = TryDecodeShiftJis(bytes) ?? Encoding.Latin1.GetString(bytes);
                }

                return ClassifyByScript(text);
            }
            catch (OperationCanceledException)
            {
                // Sniff timed out or the outer ct fired during source
                // switch. Either way we shouldn't pin "Japanese" as
                // a default — given the user's priority is English,
                // it's far less surprising to surface an unsniffed
                // file as English (worst case the user picks it,
                // sees moonrunes, picks another).
                return "eng";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Jimaku language sniff failed for {Url}", url);
                return "eng";
            }
        }

        private static string TryDecodeShiftJis(byte[] bytes)
        {
            try
            {
                // Shift-JIS isn't registered by default on .NET Core;
                // CodePagesEncodingProvider would need explicit setup.
                // Skip it gracefully when unavailable — the UTF-8 path
                // with replacement chars is already enough to classify
                // (CJK chars decode as replacement, which the script
                // counter ignores, dropping CJK ratio and surfacing as
                // English).
                return Encoding.GetEncoding(932).GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Picks a language code from the decoded text by counting
        /// characters in script-specific Unicode blocks. Hiragana or
        /// katakana → Japanese (hangul / hanzi-only get checked first
        /// so Chinese / Korean uploads don't bleed into the Japanese
        /// bucket). Falls back to English when no CJK script clears
        /// the threshold — Jimaku does host English fansubs, they
        /// just rarely advertise it in the filename.
        /// </summary>
        private static string ClassifyByScript(string text)
        {
            if (string.IsNullOrEmpty(text)) return "eng";
            int total = 0, hiragana = 0, katakana = 0, han = 0, hangul = 0;
            foreach (var c in text)
            {
                total++;
                if (c >= 0x3040 && c <= 0x309F) hiragana++;
                else if (c >= 0x30A0 && c <= 0x30FF) katakana++;
                else if (c >= 0x4E00 && c <= 0x9FFF) han++;
                else if ((c >= 0xAC00 && c <= 0xD7A3) || (c >= 0x1100 && c <= 0x11FF)) hangul++;
            }
            if (total == 0) return "eng";

            var hangulRatio = (double)hangul / total;
            if (hangulRatio >= CjkRatioThreshold) return "kor";

            // Hiragana + katakana are exclusive to Japanese, so any
            // meaningful presence pins JP regardless of han count.
            var kanaRatio = (double)(hiragana + katakana) / total;
            if (kanaRatio >= CjkRatioThreshold) return "jpn";

            // Pure-han text (no kana) is Chinese — Japanese ALWAYS
            // mixes kana with kanji in actual subtitle bodies.
            var hanRatio = (double)han / total;
            if (hanRatio >= CjkRatioThreshold) return "chi";

            return "eng";
        }

        private static string MarkerFromFilename(string filename)
        {
            foreach (var (rx, code) in StrongFilenameMarkers)
            {
                if (rx.IsMatch(filename)) return code;
            }
            return null;
        }

        private static IReadOnlyList<SubtitleTrack> BuildTracks(List<(string Code, JimakuFile File)> classified)
        {
            if (classified.Count == 0) return [];

            // Group by language, preserving Jimaku's listing order
            // within each language (last_modified DESC server-side,
            // so newest uploads surface first).
            var grouped = new Dictionary<string, List<JimakuFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (code, file) in classified)
            {
                if (!grouped.TryGetValue(code, out var list))
                {
                    list = new List<JimakuFile>();
                    grouped[code] = list;
                }
                list.Add(file);
            }

            var picked = new List<SubtitleTrack>();
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Emit(string code)
            {
                if (!grouped.TryGetValue(code, out var list)) return;
                var label = FriendlyLabel(code);
                var take = Math.Min(MaxVariantsPerLanguage, list.Count);
                for (var i = 0; i < take; i++)
                {
                    var suffix = i == 0 ? string.Empty : $" {i + 1}";
                    // Distinguish Jimaku tracks in the menu so the user
                    // can tell at a glance which provider a particular
                    // entry came from. Helps when OpenSubtitles also
                    // returned an English variant and one is timed
                    // wrong — flipping providers is one click away.
                    var displayLabel = $"{label}{suffix} (Jimaku)";
                    picked.Add(new SubtitleTrack(code, displayLabel, ProxyUrl(list[i].Url)));
                }
                emitted.Add(code);
            }

            foreach (var pref in LanguageOrder) Emit(pref);
            foreach (var kv in grouped)
            {
                if (!emitted.Contains(kv.Key)) Emit(kv.Key);
            }
            return picked;
        }

        private static string FriendlyLabel(string code)
        {
            return LanguageLabels.TryGetValue(code, out var label)
                ? label
                : code.ToUpperInvariant();
        }

        private static string ProxyUrl(string upstreamUrl)
        {
            // Re-use the unified /anime/subtitle proxy. The proxy's
            // allowlist already includes jimaku.cc, and FetchAsVttAsync
            // injects the Authorization header when the upstream host
            // matches — see OpenSubtitlesService.IsAllowedSubtitleHost
            // / BuildAuthHeader.
            var encoded = Uri.EscapeDataString(upstreamUrl);
            return $"/anime/subtitle?url={encoded}";
        }

        private record JimakuFile(string Name, string Url);
    }
}
