using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;

namespace AnimeList.Services
{
    /// <summary>
    /// Wyzie subtitle search (<c>https://sub.wyzie.ru</c>).
    /// One-call protocol: <c>GET /search?id={imdb}&amp;season={s}&amp;
    /// episode={e}&amp;language=en</c> returns a JSON array of
    /// subtitle objects already keyed by language; no per-file
    /// content sniffing required. We bias toward English at the
    /// query level (the parameter accepts ISO 639-1 codes) and
    /// drop entries whose <c>source</c> field is "OpenSubtitles"
    /// to keep this provider's contribution complementary to the
    /// dedicated <see cref="OpenSubtitlesService"/> path.
    /// </summary>
    public class WyzieSubtitlesService : IWyzieSubtitlesService
    {
        private const string ApiBase = "https://sub.wyzie.ru";
        private static readonly TimeSpan ListCacheTtl = TimeSpan.FromHours(2);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(30);

        // Cap menu length on shows where Wyzie returns a long tail
        // of mostly-identical English variants from different release
        // groups — first variant unsuffixed, 2+ get a numeric suffix.
        private const int MaxVariantsPerLanguage = 5;

        // Map Wyzie's language identifiers (mix of ISO-639-1, ISO-639-2,
        // and full English names depending on the source) to the
        // ISO-639-2/B codes the rest of the player pipeline speaks.
        private static readonly Dictionary<string, (string Code, string Label)> LanguageMap =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = ("eng", "English"),
            ["eng"] = ("eng", "English"),
            ["english"] = ("eng", "English"),
            ["es"] = ("spa", "Spanish"),
            ["spa"] = ("spa", "Spanish"),
            ["spanish"] = ("spa", "Spanish"),
            ["pt"] = ("por", "Portuguese"),
            ["por"] = ("por", "Portuguese"),
            ["portuguese"] = ("por", "Portuguese"),
            ["fr"] = ("fre", "French"),
            ["fre"] = ("fre", "French"),
            ["fra"] = ("fre", "French"),
            ["french"] = ("fre", "French"),
            ["de"] = ("ger", "German"),
            ["deu"] = ("ger", "German"),
            ["ger"] = ("ger", "German"),
            ["german"] = ("ger", "German"),
            ["it"] = ("ita", "Italian"),
            ["ita"] = ("ita", "Italian"),
            ["italian"] = ("ita", "Italian"),
            ["ja"] = ("jpn", "Japanese"),
            ["jpn"] = ("jpn", "Japanese"),
            ["japanese"] = ("jpn", "Japanese"),
            ["zh"] = ("chi", "Chinese"),
            ["chi"] = ("chi", "Chinese"),
            ["zho"] = ("chi", "Chinese"),
            ["chinese"] = ("chi", "Chinese"),
            ["ko"] = ("kor", "Korean"),
            ["kor"] = ("kor", "Korean"),
            ["korean"] = ("kor", "Korean"),
            ["und"] = ("und", "Undetermined"),
        };

        // Preferred language ordering inside the player menu —
        // English first, then the romance-language pool, then the
        // long tail.
        private static readonly string[] LanguageOrder =
            ["eng", "spa", "por", "fre", "ger", "ita", "jpn", "chi", "kor"];

        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<WyzieSubtitlesService> _logger;

        public WyzieSubtitlesService(
            IHttpClientFactory clientFactory,
            IMemoryCache cache,
            ILogger<WyzieSubtitlesService> logger)
        {
            _clientFactory = clientFactory;
            _cache = cache;
            _logger = logger;
        }

        public async Task<IReadOnlyList<SubtitleTrack>> SearchAsync(
            string imdbId, int? season, int episode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(imdbId) || !imdbId.StartsWith("tt") || episode <= 0)
            {
                return [];
            }

            var s = season ?? 1;
            var cacheKey = $"wyzie:list:{imdbId}:{s}:{episode}";
            if (_cache.TryGetValue<IReadOnlyList<SubtitleTrack>>(cacheKey, out var hit) && hit != null)
            {
                return hit;
            }

            // No language filter — Wyzie's language= parameter
            // accepts ISO codes inconsistently (some upstreams want
            // "en", others "eng", others the English name). Passing
            // any one risks filtering everything out for the
            // upstreams that disagree. Just take everything and let
            // the menu's preferred-language ordering surface English
            // first.
            var url = $"{ApiBase}/search?id={Uri.EscapeDataString(imdbId)}"
                + $"&season={s}&episode={episode}";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var client = _clientFactory.CreateClient();
                var body = await client.GetStringAsync(url, cts.Token);
                var tracks = ParseList(body);

                _cache.Set(cacheKey, tracks, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = tracks.Count == 0
                        ? NegativeCacheTtl
                        : ListCacheTtl,
                });

                // One info line per query so the operator can see the
                // language breakdown — including the empty case, so
                // it's obvious from the logs whether Wyzie was even
                // reached. The raw response length is included to
                // distinguish "Wyzie returned []" from "Wyzie returned
                // results but our parser dropped them all".
                if (tracks.Count > 0)
                {
                    var counts = string.Join(" ",
                        tracks.GroupBy(t => t.Lang)
                              .OrderBy(g => g.Key)
                              .Select(g => $"{g.Key}:{g.Count()}"));
                    _logger.LogInformation(
                        "Wyzie {Imdb} s{S}e{E}: {Count} tracks → {Counts} (raw={Bytes}B)",
                        imdbId, s, episode, tracks.Count, counts, body.Length);
                }
                else
                {
                    // Truncate so an unexpectedly-massive HTML error
                    // page doesn't flood the log; 400 chars is enough
                    // to see the JSON envelope or a CF challenge.
                    var sample = body == null ? string.Empty : body.Length > 400 ? body.Substring(0, 400) : body;
                    _logger.LogInformation(
                        "Wyzie {Imdb} s{S}e{E}: 0 tracks (raw={Bytes}B, url={Url}, body={Body})",
                        imdbId, s, episode, body == null ? 0 : body.Length, url, sample);
                }
                return tracks;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Wyzie search failed ({Imdb} s{S}e{E}).", imdbId, s, episode);
                return [];
            }
        }

        private static IReadOnlyList<SubtitleTrack> ParseList(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            JArray arr;
            try { arr = JArray.Parse(json); }
            catch { return []; }

            var entries = new List<(string Code, string Source, string Release, string Url)>();
            foreach (var token in arr)
            {
                if (token is not JObject obj) continue;

                // Wyzie's schema has shifted a few times — accept the
                // most common field names. `url` is the canonical
                // current name; `download` / `downloadUrl` are
                // historical variants we've seen on related mirrors.
                var url = TryString(obj, "url")
                       ?? TryString(obj, "download")
                       ?? TryString(obj, "downloadUrl");
                if (string.IsNullOrWhiteSpace(url)) continue;

                // Source field is just informational at this layer —
                // the outer controller dedupes by upstream URL.
                var source = (TryString(obj, "source") ?? string.Empty).Trim();

                // Language hints — Wyzie usually returns ISO-639-2/B
                // codes in `language`, but the `display` field
                // ("English", "Spanish (Latin America)", …) is a
                // useful fallback when the code is missing. If both
                // are missing the entry is still bucketed as "und"
                // so the user can still pick it from the menu.
                var rawLang = (TryString(obj, "language")
                                ?? TryString(obj, "display")
                                ?? string.Empty).Trim();
                string code = string.IsNullOrEmpty(rawLang)
                    ? "und"
                    : MapLanguage(rawLang).Code;

                var release = (TryString(obj, "release")
                                ?? TryString(obj, "display")
                                ?? string.Empty).Trim();
                entries.Add((code, source, release, url));
            }

            return GroupAndCap(entries);
        }

        private static string TryString(JObject obj, string field)
        {
            return obj.TryGetValue(field, StringComparison.OrdinalIgnoreCase, out var v)
                && v.Type != JTokenType.Null
                ? v.ToString()
                : null;
        }

        private static IReadOnlyList<SubtitleTrack> GroupAndCap(
            List<(string Code, string Source, string Release, string Url)> entries)
        {
            if (entries.Count == 0) return [];

            var grouped = new Dictionary<string, List<(string Source, string Release, string Url)>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (!grouped.TryGetValue(e.Code, out var list))
                {
                    list = new List<(string, string, string)>();
                    grouped[e.Code] = list;
                }
                list.Add((e.Source, e.Release, e.Url));
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
                    // Source tag in the label lets the user tell at a
                    // glance whether this came from Subdl / Addic7ed /
                    // etc. — useful when the timing of one variant is
                    // off and they want to try a different source's
                    // upload.
                    var srcTag = string.IsNullOrEmpty(list[i].Source)
                        ? "Wyzie"
                        : $"Wyzie · {list[i].Source}";
                    var displayLabel = $"{label}{suffix} ({srcTag})";
                    picked.Add(new SubtitleTrack(code, displayLabel, ProxyUrl(list[i].Url), "wyzie"));
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

        private static (string Code, string Label) MapLanguage(string raw)
        {
            if (LanguageMap.TryGetValue(raw, out var hit)) return hit;
            // Wyzie sometimes returns BCP-47-ish strings like "en-US";
            // strip the region tag and retry the lookup before
            // falling back to the raw value.
            var dash = raw.IndexOf('-');
            if (dash > 0 && LanguageMap.TryGetValue(raw.Substring(0, dash), out var hit2))
            {
                return hit2;
            }
            return (raw.ToLowerInvariant(), raw.ToUpperInvariant());
        }

        private static string FriendlyLabel(string code)
        {
            foreach (var kv in LanguageMap)
            {
                if (string.Equals(kv.Value.Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value.Label;
                }
            }
            return code.ToUpperInvariant();
        }

        private static string ProxyUrl(string upstreamUrl)
        {
            // /anime/subtitle is provider-agnostic and already
            // accepts wyzie.ru hosts in its allowlist — no extra
            // wiring needed.
            var encoded = Uri.EscapeDataString(upstreamUrl);
            return $"/anime/subtitle?url={encoded}";
        }
    }
}
