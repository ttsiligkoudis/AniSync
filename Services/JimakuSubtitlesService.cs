using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
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
    /// <c>"Show.S01E03.srt"</c>). Language is inferred from filename
    /// markers; Japanese is the default when nothing else is tagged
    /// (Jimaku is a JP-learner-leaning archive).
    /// </summary>
    public class JimakuSubtitlesService : IJimakuSubtitlesService
    {
        private const string ApiBase = "https://jimaku.cc/api";

        private static readonly TimeSpan ListCacheTtl = TimeSpan.FromHours(2);
        private static readonly TimeSpan NoKeyLogOnceWindow = TimeSpan.FromHours(1);

        // Same shape OpenSubtitlesService uses — caps the menu length
        // for popular shows where Jimaku has many crowd uploads. First
        // variant is unlabelled, 2+ get a numeric suffix.
        private const int MaxVariantsPerLanguage = 5;

        // Friendly names for the language picker. Jimaku doesn't
        // return a structured lang code, so we sniff filenames for
        // ISO-639-1/2/3 markers and English-name fragments and map
        // them through this table. Anything we can't classify is
        // bucketed as Japanese (Jimaku's default upload language).
        private static readonly (Regex Pattern, string Code, string Label)[] LanguageRules =
        [
            (new Regex(@"(?<![A-Za-z])(?:en|eng|english)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "eng", "English"),
            (new Regex(@"(?<![A-Za-z])(?:es|spa|esp|spanish|español)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "spa", "Spanish"),
            (new Regex(@"(?<![A-Za-z])(?:pt|por|portuguese|portugu[eê]s)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "por", "Portuguese"),
            (new Regex(@"(?<![A-Za-z])(?:fr|fre|fra|french|français)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "fre", "French"),
            (new Regex(@"(?<![A-Za-z])(?:de|ger|deu|german|deutsch)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ger", "German"),
            (new Regex(@"(?<![A-Za-z])(?:it|ita|italian|italiano)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ita", "Italian"),
            (new Regex(@"(?<![A-Za-z])(?:zh|chi|zho|chinese|中文)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "chi", "Chinese"),
            (new Regex(@"(?<![A-Za-z])(?:ko|kor|korean|한국어)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "kor", "Korean"),
            (new Regex(@"(?<![A-Za-z])(?:ja|jp|jpn|japanese|日本語)(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "jpn", "Japanese"),
        ];

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
                cts.CancelAfter(TimeSpan.FromSeconds(8));
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
                var tracks = BuildTracks(matched);

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

        private static IReadOnlyList<SubtitleTrack> BuildTracks(List<JimakuFile> files)
        {
            if (files.Count == 0) return [];

            // Group by detected language, preserving Jimaku's listing
            // order within each language (last_modified DESC server-
            // side, so newest uploads surface first).
            var grouped = new Dictionary<string, List<JimakuFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var (code, _) = DetectLanguage(f.Name);
                if (!grouped.TryGetValue(code, out var list))
                {
                    list = new List<JimakuFile>();
                    grouped[code] = list;
                }
                list.Add(f);
            }

            var picked = new List<SubtitleTrack>();
            foreach (var kv in grouped)
            {
                var label = FriendlyLabel(kv.Key);
                var take = Math.Min(MaxVariantsPerLanguage, kv.Value.Count);
                for (var i = 0; i < take; i++)
                {
                    var suffix = i == 0 ? string.Empty : $" {i + 1}";
                    // Distinguish Jimaku tracks in the menu so the user
                    // can tell at a glance which provider a particular
                    // entry came from. Helps when OpenSubtitles also
                    // returned an English variant and one is timed
                    // wrong — flipping providers is one click away.
                    var displayLabel = $"{label}{suffix} (Jimaku)";
                    picked.Add(new SubtitleTrack(kv.Key, displayLabel, ProxyUrl(kv.Value[i].Url)));
                }
            }
            return picked;
        }

        private static (string Code, string Label) DetectLanguage(string filename)
        {
            foreach (var (rx, code, label) in LanguageRules)
            {
                if (rx.IsMatch(filename)) return (code, label);
            }
            // Jimaku's house default is JP — most uploads are Japanese
            // closed captions for language learners.
            return ("jpn", "Japanese");
        }

        private static string FriendlyLabel(string code)
        {
            foreach (var (_, c, label) in LanguageRules)
            {
                if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return label;
            }
            return code.ToUpperInvariant();
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
