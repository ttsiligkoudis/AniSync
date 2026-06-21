using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System.Globalization;
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

            // Cached VTT served from memory for the (long-ish) lifetime
            // of the sub URL. Important for the external-launch flow:
            // strem.io's subs5 endpoint is rate-limited per Fly IP and
            // a second fetch for the same URL within a couple minutes
            // tends to 502, even when the first one succeeded. Caching
            // lets the watch page's in-browser player and the
            // external-launch sidecar both come off one upstream
            // round-trip. Cache key includes the full URL since
            // OpenSubtitles' "subencoding-stremio-utf8" transform is
            // path-bearing — different paths fetch different bytes.
            var cacheKey = "subtitle-vtt:" + url;
            if (_cache.TryGetValue<string>(cacheKey, out var cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
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
                // VTT spec only supports <c>, <i>, <b>, <u>, <ruby>, <rt>,
                // <v>, <lang>, plus class spans. <font ...> is a SubRip
                // (SRT) leftover — many providers ship .vtt files with
                // <font face="…" size="…"> wrappers anyway. ArtPlayer's
                // renderer prints unknown tags as literal text instead of
                // ignoring them, so the line "It's so far away" shows up
                // as "&lt;font face=...&gt;It's so far away&lt;/font&gt;"
                // on screen. Strip the opening + closing tags (preserve
                // the inner text) before returning.
                vtt = StripFontTags(vtt);
                // Lift positioning out of the ASS override blocks BEFORE we strip
                // them: an \an8 / \pos(x,y) on a cue means it's a positioned sign
                // translation or song overlay, not bottom dialogue. Convert those to
                // WebVTT cue settings (line/position/align) on the cue header so the
                // renderer puts them where the author intended, instead of collapsing
                // every cue to the bottom where they overlap the dialogue. Mirrors the
                // embedded-MKV converter's buildVttCueSettings (Watch.cshtml).
                vtt = ApplyAssPositioning(vtt);
                // ASS / SSA override blocks ({\an8}, {\fad(200,200)},
                // {\pos(x,y)}, {\c&Hxxxxxx&}, …) leak through when an
                // OpenSubtitles release re-packages an .ass track as
                // .srt / .vtt without normalising the body — common
                // for anime where the original sub author authored in
                // ASS for typesetting / karaoke. Native VTT renderers
                // have no concept of these tags, so they paint them as
                // literal text on the cue. Positioning was already lifted to the
                // header above; now drop the (consumed) blocks from the cue text.
                vtt = StripAssOverrides(vtt);
                // Lift cues off the bottom edge so they don't collide
                // with ArtPlayer's controls bar / sit flush against
                // the video frame. Provider VTTs almost never set a
                // line position, so they all render at line:auto
                // (~bottom). Uses snap-to-lines integer mode (line:-3
                // = third row from bottom) rather than percentages —
                // Chromium browsers appear to ignore percentage line
                // offsets for bottom-anchored default cues; the
                // integer form is honoured uniformly. Cues that
                // already declared a line stay untouched.
                vtt = ApplyDefaultLineMargin(vtt);

                // Cache the final VTT body keyed by upstream URL.
                // 15-minute TTL covers a normal "open + watch + maybe
                // re-open externally" session window; short enough
                // that an actually-rotated URL doesn't get pinned to
                // a stale response, long enough that the rate-limit
                // window passes before the next cache miss.
                _cache.Set(cacheKey, vtt, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
                });
                return vtt;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subtitle fetch failed ({Url}).", SafeHost(url));
                return null;
            }
        }

        public async Task<string> FetchAsAssAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!IsAllowedSubtitleHost(url))
            {
                _logger.LogWarning("Subtitle proxy refused host: {Host}", SafeHost(url));
                return null;
            }

            var cacheKey = "subtitle-ass:" + url;
            if (_cache.TryGetValue<string>(cacheKey, out var cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            var fetchUrl = EnsureUtf8Transform(url);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var client = _clientFactory.CreateClient();
                var bytes = await client.GetByteArrayAsync(fetchUrl, cts.Token);
                var text = DecodeText(bytes);

                string ass;
                if (LooksLikeAss(text))
                {
                    // Already ASS/SSA — hand it to libVLC untouched so libass renders the
                    // author's own styles + positioning against the file's real PlayResX/Y.
                    ass = text.TrimStart('﻿');
                }
                else
                {
                    // SRT / VTT (often a re-packaged ASS with the positioning left inline as
                    // {\an8}/{\pos(…)} in the cue text). Normalise to VTT, then wrap each cue
                    // in an ASS Dialogue line that keeps those overrides inline — libass then
                    // positions the signs while plain dialogue stays at the bottom (\an2).
                    var vtt = text.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                        ? text
                        : SrtToVtt(text);
                    vtt = StripFontTags(vtt);
                    ass = VttToAss(vtt);
                }

                _cache.Set(cacheKey, ass, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
                });
                return ass;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subtitle fetch failed ({Url}).", SafeHost(url));
                return null;
            }
        }

        // Cheap sniff for an ASS/SSA document so we can pass it through untouched
        // (preserving the author's styles + PlayRes) instead of rebuilding it.
        private static bool LooksLikeAss(string text) =>
            !string.IsNullOrEmpty(text)
            && (text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase)
                || text.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)
                || text.Contains("[Events]", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase));

        // Minimal ASS preamble. PlayResX/Y match the web converter's fallback (1920×1080)
        // so a \pos authored without a declared canvas lands consistently across heads;
        // a single Default style anchored bottom-centre (\an2) is the dialogue baseline,
        // and per-cue {\an…}/{\pos…} overrides move signs off it.
        private const string AssPreamble =
            "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n" +
            "[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            "Style: Default,Arial,54,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,40,40,40,1\n\n" +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";

        // Two timestamps of a VTT cue header: "[HH:]MM:SS.mmm --> [HH:]MM:SS.mmm".
        private static readonly Regex VttCueTimes = new(
            @"^((?:\d{2}:)?\d{2}:\d{2}\.\d{3})\s+-->\s+((?:\d{2}:)?\d{2}:\d{2}\.\d{3})",
            RegexOptions.Compiled);

        // Builds an ASS document from a WebVTT body, one Dialogue line per cue. ASS override
        // blocks ({\an8}, {\pos…}) already inline in the cue text are kept verbatim (libass
        // renders them); basic HTML styling is mapped to ASS tags and any other tag dropped.
        private static string VttToAss(string vtt)
        {
            var sb = new StringBuilder(AssPreamble);
            if (string.IsNullOrEmpty(vtt)) return sb.ToString();
            var lines = vtt.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var m = VttCueTimes.Match(lines[i]);
                if (!m.Success) continue;
                var start = VttTsToAss(m.Groups[1].Value);
                var end = VttTsToAss(m.Groups[2].Value);

                var text = new StringBuilder();
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (string.IsNullOrWhiteSpace(lines[j]) || VttCueTimes.IsMatch(lines[j])) break;
                    if (text.Length > 0) text.Append("\\N");
                    text.Append(lines[j]);
                }
                var body = ConvertVttInlineTags(text.ToString());
                if (body.Length == 0) continue;
                sb.Append("Dialogue: 0,").Append(start).Append(',').Append(end)
                  .Append(",Default,,0,0,0,,").Append(body).Append('\n');
            }
            return sb.ToString();
        }

        // "[HH:]MM:SS.mmm" → ASS "H:MM:SS.cc" (centiseconds).
        private static string VttTsToAss(string ts)
        {
            var parts = ts.Split(':');
            var sec = parts[^1].Split('.');
            int hh = parts.Length == 3 && int.TryParse(parts[0], out var h) ? h : 0;
            int mm = int.TryParse(parts[^2], out var m) ? m : 0;
            int ss = int.TryParse(sec[0], out var s) ? s : 0;
            int ms = sec.Length > 1 && int.TryParse(sec[1], out var x) ? x : 0;
            long totalMs = (((long)hh * 3600) + (mm * 60) + ss) * 1000 + ms;
            long oh = totalMs / 3600000; totalMs %= 3600000;
            long om = totalMs / 60000; totalMs %= 60000;
            long os = totalMs / 1000; long ocs = (totalMs % 1000) / 10;
            return $"{oh}:{om:D2}:{os:D2}.{ocs:D2}";
        }

        private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);

        // Keep inline ASS overrides ({…}); translate the handful of HTML styling tags VTT
        // uses to their ASS equivalents and drop any other tag so it doesn't print literally.
        private static string ConvertVttInlineTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = text
                .Replace("<i>", "{\\i1}", StringComparison.OrdinalIgnoreCase).Replace("</i>", "{\\i0}", StringComparison.OrdinalIgnoreCase)
                .Replace("<b>", "{\\b1}", StringComparison.OrdinalIgnoreCase).Replace("</b>", "{\\b0}", StringComparison.OrdinalIgnoreCase)
                .Replace("<u>", "{\\u1}", StringComparison.OrdinalIgnoreCase).Replace("</u>", "{\\u0}", StringComparison.OrdinalIgnoreCase);
            text = HtmlTag.Replace(text, string.Empty);
            return text.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Trim();
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
            // Relative /api/v1/subtitle?url=... — same-origin as the host page so the <track> tag loads
            // without a CORS opt-in. MUST be the MetaProxyController route (admitted on both heads), NOT
            // /meta/subtitle: the web head filters MetaController out (its /meta/* routes collide with the
            // Blazor Detail page), so /meta/subtitle 404s there and subtitles silently never render.
            var encoded = Uri.EscapeDataString(upstreamUrl);
            return $"/api/v1/subtitle?url={encoded}";
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
            //
            // Domain match is `host == domain || host endswith "." + domain`
            // rather than a bare suffix check — a bare `EndsWith("strem.io")`
            // would also match an attacker-controlled `evilstrem.io`, which is
            // exactly the substring trap a host allowlist must not fall into.
            return host == "subs5.strem.io"
                || MatchesDomain(host, "opensubtitles.org")
                || MatchesDomain(host, "opensubtitles.com")
                || MatchesDomain(host, "opensubtitles-v3.strem.io")
                || MatchesDomain(host, "strem.io")
                || MatchesDomain(host, "subdl.com");
        }

        private static bool MatchesDomain(string host, string domain) =>
            host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

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

        // Matches <font ...> / </font> in any case, with any attribute soup
        // (including the unquoted `size=24` form some converters emit).
        // Singleline so a stray newline inside the attribute list doesn't
        // leave half the tag behind.
        private static readonly Regex FontTag = new(
            @"<\s*/?\s*font\b[^>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static string StripFontTags(string vtt) =>
            string.IsNullOrEmpty(vtt) ? vtt : FontTag.Replace(vtt, string.Empty);

        // ASS / SSA override block: "{" + at least one backslash-
        // prefixed tag (\an8, \pos, \fad, \c&H…&, \b1, …) + "}". The
        // negated char class on the body bounds the match non-greedy,
        // so two adjacent overrides on the same line ("{\an8}{\fad
        // (200,200)}Lyrics…") strip independently instead of swallowing
        // the text between them. Requiring the backslash leaves
        // legitimate dialogue like "He muttered {something}" untouched
        // — ASS overrides are the only case in this corpus where
        // braces are syntax rather than text.
        private static readonly Regex AssOverride = new(
            @"\{\\[^}]*\}",
            RegexOptions.Compiled);

        private static string StripAssOverrides(string vtt) =>
            string.IsNullOrEmpty(vtt) ? vtt : AssOverride.Replace(vtt, string.Empty);

        // ASS positioning tags inside an override block. \an1..9 = numpad anchor;
        // legacy \a (bitmap alignment); \pos(x,y) = absolute coords; \move(x1,y1,…)
        // animates — VTT can't animate, so we snap to the start coords. Same set the
        // web converter's buildVttCueSettings reads.
        private static readonly Regex AssAn = new(@"\\an([1-9])", RegexOptions.Compiled);
        private static readonly Regex AssLegacyA = new(@"\\a(\d+)", RegexOptions.Compiled);
        private static readonly Regex AssPos = new(@"\\pos\(\s*([\d.\-]+)\s*,\s*([\d.\-]+)\s*\)", RegexOptions.Compiled);
        private static readonly Regex AssMove = new(@"\\move\(\s*([\d.\-]+)\s*,\s*([\d.\-]+)", RegexOptions.Compiled);

        // Re-packaged ASS rarely keeps a [Script Info] PlayResX/Y, so \pos coords have
        // no declared canvas to scale against. Assume 1920×1080 — the same default the
        // web converter (parseAssPlayRes) falls back to, so both heads agree.
        private const double AssPlayResX = 1920;
        private const double AssPlayResY = 1080;

        /// <summary>
        /// Walks each VTT cue and, when its text carries ASS positioning overrides
        /// (\an / \pos / \move), appends the equivalent WebVTT cue settings
        /// (line / position / align) to that cue's timing header. Cues the source
        /// already positioned (a <c>line:</c> in the header) are left untouched, as
        /// are plain bottom-dialogue cues (picked up later by ApplyDefaultLineMargin).
        /// The override blocks themselves are stripped from the text afterwards by
        /// StripAssOverrides.
        /// </summary>
        private static string ApplyAssPositioning(string vtt)
        {
            if (string.IsNullOrEmpty(vtt) || vtt.IndexOf('{') < 0) return vtt; // no overrides at all
            var lines = vtt.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var header = CueHeader.Match(lines[i]);
                if (!header.Success) continue;
                // Source already set an explicit line position — respect it.
                if (header.Groups[2].Value.IndexOf("line:", StringComparison.Ordinal) >= 0) continue;

                // Concatenate the override blocks across the cue's text lines (an ASS
                // author may split them, e.g. "{\an8}{\fad(200,200)}Lyrics…").
                var overrides = new StringBuilder();
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (string.IsNullOrWhiteSpace(lines[j]) || CueHeader.IsMatch(lines[j])) break;
                    foreach (Match ov in AssOverride.Matches(lines[j]))
                        overrides.Append(ov.Value);
                }
                if (overrides.Length == 0) continue;

                var settings = BuildVttCueSettings(overrides.ToString());
                if (settings.Length == 0) continue;
                lines[i] = header.Groups[1].Value + header.Groups[2].Value + settings;
            }
            return string.Join("\n", lines);
        }

        // ASS override text → " line:.. position:.. align:..". Ported verbatim from the
        // web converter's buildVttCueSettings + vttSettingsForAlign so a sub routed
        // through either path lands in the same on-screen spot. Empty when there's no
        // positioning to translate.
        private static string BuildVttCueSettings(string overrideText)
        {
            if (string.IsNullOrEmpty(overrideText)) return string.Empty;
            string? line = null, position = null, align = null;

            var anM = AssAn.Match(overrideText);
            var posM = AssPos.Match(overrideText);
            var moveM = AssMove.Match(overrideText);
            var posSource = posM.Success ? posM : (moveM.Success ? moveM : null);

            if (posSource != null
                && double.TryParse(posSource.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawX)
                && double.TryParse(posSource.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawY))
            {
                var px = Math.Clamp(rawX / AssPlayResX * 100, 0, 100);
                var py = Math.Clamp(rawY / AssPlayResY * 100, 0, 100);
                position = px.ToString("0.0", CultureInfo.InvariantCulture) + "%";
                line = py.ToString("0.0", CultureInfo.InvariantCulture) + "%";
                if (anM.Success)
                {
                    var anA = int.Parse(anM.Groups[1].Value);
                    align = anA is 1 or 4 or 7 ? "start" : anA is 3 or 6 or 9 ? "end" : "center";
                }
            }
            else if (anM.Success)
            {
                (line, position, align) = VttSettingsForAlign(int.Parse(anM.Groups[1].Value));
            }
            else
            {
                var aM = AssLegacyA.Match(overrideText);
                // SSA legacy \a: 1=left,2=centre,3=right (+4 top, +8 middle). Map to the
                // \an numpad so the same alignment helper applies.
                if (aM.Success && int.TryParse(aM.Groups[1].Value, out var legacy)
                    && LegacyAToAn.TryGetValue(legacy, out var mapped))
                    (line, position, align) = VttSettingsForAlign(mapped);
            }

            var parts = new List<string>();
            if (line != null) parts.Add("line:" + line);
            if (position != null) parts.Add("position:" + position);
            if (align != null) parts.Add("align:" + align);
            return parts.Count > 0 ? " " + string.Join(" ", parts) : string.Empty;
        }

        private static readonly Dictionary<int, int> LegacyAToAn = new()
        {
            { 1, 1 }, { 2, 2 }, { 3, 3 }, { 5, 7 }, { 6, 8 }, { 7, 9 }, { 9, 4 }, { 10, 5 }, { 11, 6 },
        };

        // ASS numpad anchor (1=bottom-left … 9=top-right) → WebVTT (line, position,
        // align). null = the VTT default for that axis (bottom / centre).
        private static (string? Line, string? Position, string? Align) VttSettingsForAlign(int an)
        {
            string? line = an is 7 or 8 or 9 ? "5%" : an is 4 or 5 or 6 ? "50%" : null;
            string? position, align;
            if (an is 1 or 4 or 7) { position = "5%"; align = "start"; }
            else if (an is 3 or 6 or 9) { position = "95%"; align = "end"; }
            else { position = null; align = null; }
            return (line, position, align);
        }

        // Cue-header lines look like "00:00:01.500 --> 00:00:04.000"
        // optionally followed by space-separated cue settings. WebVTT
        // also allows the short form "MM:SS.mmm --> MM:SS.mmm" when no
        // timestamp crosses an hour — many OpenSubtitles tracks use
        // that form for short episodes, so the hours half is optional.
        // Captures any existing settings tail so we can decide whether
        // to append our default.
        private static readonly Regex CueHeader = new(
            @"^((?:\d{2}:)?\d{2}:\d{2}\.\d{3}\s+-->\s+(?:\d{2}:)?\d{2}:\d{2}\.\d{3})([^\r\n]*)$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Appends <c>line:-2</c> (snap-to-lines, second row from
        /// bottom) to every VTT cue header that doesn't already carry
        /// a <c>line:</c> setting. Lifts the cue's bottom edge one
        /// text-line height up from the video's bottom edge — small
        /// breathing room so dialogue doesn't sit flush against the
        /// player frame / ArtPlayer controls bar. Uses the integer
        /// snap-to-lines mode rather than a percentage because
        /// Chromium browsers appear to ignore percentage line offsets
        /// for bottom-anchored default cues — the integer form is
        /// honoured uniformly across browsers. Cues that did set a
        /// line (sign translations, song-lyric overlays) stay
        /// untouched.
        /// </summary>
        private static string ApplyDefaultLineMargin(string vtt)
        {
            if (string.IsNullOrEmpty(vtt)) return vtt;
            return CueHeader.Replace(vtt, m =>
            {
                var settings = m.Groups[2].Value;
                // Already positioned by the source — leave it alone.
                if (settings.IndexOf("line:", StringComparison.Ordinal) >= 0) return m.Value;
                return m.Groups[1].Value + settings + " line:-2";
            });
        }
    }
}
