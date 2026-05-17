using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeList.Services
{
    /// <summary>
    /// Talks to the opensubtitles-v3 Stremio addon at
    /// <c>https://opensubtitles-v3.strem.io</c> — the default subtitle
    /// addon Stremio installs ship with. URL shape:
    /// <c>/subtitles/{type}/{stremioId}.json</c>; response payload is
    /// <c>{ subtitles: [{ id, url, lang }] }</c> per the Stremio addon
    /// spec. Subtitle URLs come back pointing at the upstream
    /// providers (opensubtitles.com / .org / .stream); we proxy each
    /// fetch through /anime/subtitle to keep the &lt;track&gt; load
    /// same-origin and to convert SRT to WebVTT inline.
    /// </summary>
    public class OpenSubtitlesService : ISubtitleService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<OpenSubtitlesService> _logger;

        private const string AddonBase = "https://opensubtitles-v3.strem.io";
        private static readonly TimeSpan ListCacheTtl = TimeSpan.FromHours(2);
        private static readonly TimeSpan VttCacheTtl  = TimeSpan.FromHours(6);

        // Common language pool surfaced first in the player's captions
        // menu. The addon returns ISO-639-2/B codes (eng, spa, fre …);
        // the first match per language wins, with everything else
        // appended after for completionism.
        private static readonly string[] PreferredLanguages =
        [
            "eng", "spa", "por", "fre", "ger", "ita", "rus", "ara", "jpn",
        ];

        // Pretty-print labels for the language picker. ISO-639-2/B
        // codes are unfriendly in a UI; falls back to upper-casing the
        // raw code when we don't have a mapping (anime occasionally
        // gets exotic language entries from the addon).
        private static readonly Dictionary<string, string> LanguageLabels =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "English",
            ["spa"] = "Spanish",
            ["por"] = "Portuguese",
            ["fre"] = "French",
            ["fra"] = "French",
            ["ger"] = "German",
            ["deu"] = "German",
            ["ita"] = "Italian",
            ["rus"] = "Russian",
            ["ara"] = "Arabic",
            ["jpn"] = "Japanese",
            ["chi"] = "Chinese",
            ["zho"] = "Chinese",
            ["kor"] = "Korean",
            ["tur"] = "Turkish",
            ["pol"] = "Polish",
            ["dut"] = "Dutch",
            ["nld"] = "Dutch",
            ["swe"] = "Swedish",
            ["fin"] = "Finnish",
            ["nor"] = "Norwegian",
            ["dan"] = "Danish",
            ["heb"] = "Hebrew",
            ["hun"] = "Hungarian",
            ["ces"] = "Czech",
            ["cze"] = "Czech",
            ["rum"] = "Romanian",
            ["ron"] = "Romanian",
            ["bul"] = "Bulgarian",
            ["gre"] = "Greek",
            ["ell"] = "Greek",
            ["tha"] = "Thai",
            ["vie"] = "Vietnamese",
            ["ind"] = "Indonesian",
            ["may"] = "Malay",
            ["msa"] = "Malay",
        };

        public OpenSubtitlesService(
            IHttpClientFactory clientFactory,
            IMemoryCache cache,
            ILogger<OpenSubtitlesService> logger)
        {
            _clientFactory = clientFactory;
            _cache = cache;
            _logger = logger;
        }

        public async Task<IReadOnlyList<SubtitleTrack>> SearchAsync(
            string imdbId, int? season, int? episode, string filename = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(imdbId) || !imdbId.StartsWith("tt"))
            {
                return [];
            }

            // Filename narrows results to release-matched subs — same
            // signal Stremio's web player passes. Normalise to a
            // short, stable cache fingerprint so e.g. two slightly-
            // different display titles of the same file don't churn
            // cache entries (we lowercase and strip the extension).
            var normalisedFilename = !string.IsNullOrWhiteSpace(filename)
                ? filename.Trim().ToLowerInvariant()
                : null;
            var filenameKey = string.IsNullOrEmpty(normalisedFilename) ? "_" : normalisedFilename;
            var cacheKey = $"opensubs:list:{imdbId}:{season ?? 0}:{episode ?? 0}:{filenameKey}";
            if (_cache.TryGetValue<IReadOnlyList<SubtitleTrack>>(cacheKey, out var hit) && hit != null)
            {
                return hit;
            }

            // Stremio addon URL shape: /subtitles/{type}/{stremioId}.json
            // - series → tt{imdb}:{s}:{e}
            // - movie  → tt{imdb}
            // When we know the source filename, append
            // /filename={encoded}.json — the addon spec passes that
            // through to OpenSubtitles for release-aware matching, so
            // the timing of the returned subtitles actually lines up
            // with the user's file (this is what fixes the "subs are
            // off" symptom).
            string streamioPath;
            if (episode.HasValue && episode.Value > 0)
            {
                var s = season ?? 1;
                streamioPath = $"series/{Uri.EscapeDataString($"{imdbId}:{s}:{episode.Value}")}";
            }
            else
            {
                streamioPath = $"movie/{Uri.EscapeDataString(imdbId)}";
            }

            var url = string.IsNullOrEmpty(normalisedFilename)
                ? $"{AddonBase}/subtitles/{streamioPath}.json"
                : $"{AddonBase}/subtitles/{streamioPath}/filename={Uri.EscapeDataString(normalisedFilename)}.json";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var client = _clientFactory.CreateClient();
                var raw = await client.GetStringAsync(url, cts.Token);
                var parsed = ParseList(raw);
                _cache.Set(cacheKey, parsed, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ListCacheTtl });
                return parsed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenSubtitles addon search failed ({Imdb} s{S}e{E}).", imdbId, season, episode);
                return [];
            }
        }

        public async Task<string> FetchAsVttAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            // Hard-cap to known subtitle providers so the proxy can't
            // be turned into a generic SSRF tool. The addon mostly
            // returns opensubtitles.* URLs, with a long tail to
            // strem.io's own proxy and a handful of legacy CDNs.
            if (!IsAllowedSubtitleHost(url))
            {
                _logger.LogWarning("Subtitle proxy refused host: {Host}", SafeHost(url));
                return null;
            }

            // Force the subs5.strem.io UTF-8 transform when applicable
            // (no-op for other hosts). Cache key still keys on the
            // pre-transform URL so re-requests with the same upstream
            // URL hit cache regardless of transform application.
            var cacheKey = $"opensubs:vtt:{url}";
            if (_cache.TryGetValue<string>(cacheKey, out var hit) && hit != null)
            {
                return hit;
            }
            var fetchUrl = EnsureUtf8Transform(url);

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var client = _clientFactory.CreateClient();
                var bytes = await client.GetByteArrayAsync(fetchUrl, cts.Token);
                var text = DecodeText(bytes);
                var vtt = text.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                    ? text
                    : SrtToVtt(text);
                _cache.Set(cacheKey, vtt, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = VttCacheTtl });
                return vtt;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subtitle fetch failed ({Url}).", SafeHost(url));
                return null;
            }
        }

        // Cap on how many entries we surface per language. The addon
        // can return 10+ "English" variants for popular shows; menu
        // sanity wins over completeness. Variant 1 is unlabelled
        // ("English"), variants 2+ get a numeric suffix
        // ("English 2", "English 3", …).
        private const int MaxVariantsPerLanguage = 5;

        /// <summary>
        /// Parses the addon's <c>{ subtitles: [{ id, url, lang }] }</c>
        /// response. Emits multiple entries per language so the user
        /// can pick a different variant when the first one's timing /
        /// translation is off. The addon returns variants in its own
        /// quality order, so taking them in that order keeps the
        /// best-guess pick first.
        /// </summary>
        private static IReadOnlyList<SubtitleTrack> ParseList(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            dynamic root = DeserializeObject<dynamic>(json);
            if (root?.subtitles == null) return [];

            var entries = new List<(string Lang, string Url)>();
            foreach (var s in root.subtitles)
            {
                var url = (string)s.url;
                if (string.IsNullOrWhiteSpace(url)) continue;
                var lang = ((string)s.lang ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(lang)) continue;
                entries.Add((lang, url));
            }

            // Group by language preserving the addon's emit order
            // within each group (so variant 1 stays the addon-ranked
            // top hit).
            var byLang = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (!byLang.TryGetValue(e.Lang, out var list))
                {
                    list = new List<string>();
                    byLang[e.Lang] = list;
                }
                list.Add(e.Url);
            }

            var picked = new List<SubtitleTrack>();
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void EmitGroup(string lang, List<string> urls)
            {
                var baseLabel = FriendlyLabel(lang);
                var take = Math.Min(MaxVariantsPerLanguage, urls.Count);
                for (var i = 0; i < take; i++)
                {
                    var label = i == 0 ? baseLabel : $"{baseLabel} {i + 1}";
                    picked.Add(new SubtitleTrack(lang, label, ProxyUrl(urls[i]), "opensubtitles"));
                }
                emitted.Add(lang);
            }

            // Preferred languages first (English at the top, then
            // Spanish / Portuguese / French / …), each with up to
            // MaxVariantsPerLanguage entries.
            foreach (var pref in PreferredLanguages)
            {
                if (byLang.TryGetValue(pref, out var urls))
                {
                    EmitGroup(pref, urls);
                }
            }
            // Long tail: every language the addon returned that
            // wasn't already emitted above.
            foreach (var kv in byLang)
            {
                if (!emitted.Contains(kv.Key))
                {
                    EmitGroup(kv.Key, kv.Value);
                }
            }
            return picked;
        }

        private static string FriendlyLabel(string lang)
        {
            if (LanguageLabels.TryGetValue(lang, out var pretty)) return pretty;
            return lang.ToUpperInvariant();
        }

        private static string ProxyUrl(string upstreamUrl)
        {
            // Relative /anime/subtitle?url=... — same-origin as the
            // host page so the <track> tag loads without needing a
            // CORS opt-in on the player.
            var encoded = Uri.EscapeDataString(upstreamUrl);
            return $"/anime/subtitle?url={encoded}";
        }

        private static bool IsAllowedSubtitleHost(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            var host = u.Host.ToLowerInvariant();
            // subs5.strem.io is the actual SRT-serving host the
            // opensubtitles-v3 addon returns URLs for; the addon itself
            // lives on opensubtitles-v3.strem.io. Both call through the
            // strem.io suffix anyway, but listing the specific subs
            // host first documents the chain explicitly.
            return host == "subs5.strem.io"
                || host.EndsWith("opensubtitles.org")
                || host.EndsWith("opensubtitles.com")
                || host.EndsWith("opensubtitles-v3.strem.io")
                || host.EndsWith("strem.io")
                || host.EndsWith("subdl.com")
                || host.EndsWith("wyzie.ru");   // Wyzie aggregator + its own subtitle CDN
        }

        /// <summary>
        /// Inserts subs5.strem.io's <c>subencoding-stremio-utf8</c>
        /// transform into the path when it's missing. The transform
        /// asks the server to re-encode the subtitle as UTF-8 inline,
        /// avoiding the Latin-1 / Windows-1252 guess we'd otherwise
        /// do client-side in DecodeText(). Stremio's player injects
        /// this segment itself; our proxy mirrors that.
        /// </summary>
        private static string EnsureUtf8Transform(string url)
        {
            const string transform = "/subencoding-stremio-utf8/";
            if (string.IsNullOrEmpty(url) || url.Contains(transform, StringComparison.Ordinal))
            {
                return url;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
            // Scoped to subs5.strem.io — other providers (opensubtitles.com /
            // .org / subdl) serve their own URL shapes and don't accept this
            // transform.
            if (!u.Host.Equals("subs5.strem.io", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
            // Stremio's transform sits after /<lang>/download/ — for
            // example /en/download/src-api/file/123 →
            // /en/download/subencoding-stremio-utf8/src-api/file/123.
            const string anchor = "/download/";
            var idx = u.AbsolutePath.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return url;
            var newPath = u.AbsolutePath.Insert(idx + anchor.Length, "subencoding-stremio-utf8/");
            var builder = new UriBuilder(u) { Path = newPath };
            return builder.Uri.ToString();
        }

        private static string SafeHost(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "<invalid>";
        }

        private static string DecodeText(byte[] bytes)
        {
            try
            {
                return Encoding.GetEncoding("utf-8", new EncoderExceptionFallback(), new DecoderExceptionFallback())
                    .GetString(bytes);
            }
            catch
            {
                return Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
            }
        }

        // SRT → VTT differs mainly in a "WEBVTT" header line and
        // timestamps using "." instead of "," as the millisecond
        // separator. Cue text is copied verbatim. Lenient browsers
        // accept raw SRT served as text/vtt but Firefox does not.
        private static readonly Regex SrtTimestamp = new(
            @"(\d{2}:\d{2}:\d{2}),(\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}),(\d{3})",
            RegexOptions.Compiled);

        private static string SrtToVtt(string srt)
        {
            var body = SrtTimestamp.Replace(srt, m =>
                $"{m.Groups[1].Value}.{m.Groups[2].Value} --> {m.Groups[3].Value}.{m.Groups[4].Value}");
            return "WEBVTT\n\n" + body.Replace("\r\n", "\n");
        }
    }
}
