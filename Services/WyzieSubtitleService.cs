using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeList.Services
{
    /// <summary>
    /// Talks to <c>https://sub.wyzie.ru</c> — the free aggregator most
    /// Stremio subtitle addons proxy through. Returns subtitle tracks
    /// per (imdb, season, episode) and converts SRT to VTT on the fly
    /// since browser &lt;track&gt; elements require WebVTT.
    /// </summary>
    public class WyzieSubtitleService : IWyzieSubtitleService
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<WyzieSubtitleService> _logger;

        private const string SearchBase = "https://sub.wyzie.ru/search";
        private static readonly TimeSpan ListCacheTtl = TimeSpan.FromHours(2);
        private static readonly TimeSpan VttCacheTtl  = TimeSpan.FromHours(6);

        // Wyzie supports a wide language pool — we keep it small so the
        // captions menu stays scannable. Order is preference order: the
        // first match for each language code wins, so an "eng" track at
        // index 0 beats a worse-quality "eng" at index 3.
        private static readonly string[] PreferredLanguages =
        [
            "eng", "en", "spa", "es", "por", "pt", "fre", "fr", "ger", "de",
            "ita", "it", "rus", "ru", "ara", "ar", "jpn", "ja",
        ];

        public WyzieSubtitleService(
            IHttpClientFactory clientFactory,
            IMemoryCache cache,
            ILogger<WyzieSubtitleService> logger)
        {
            _clientFactory = clientFactory;
            _cache = cache;
            _logger = logger;
        }

        public async Task<IReadOnlyList<WyzieSubtitleTrack>> SearchAsync(string imdbId, int? season, int? episode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(imdbId) || !imdbId.StartsWith("tt"))
            {
                return [];
            }

            var cacheKey = $"wyzie:list:{imdbId}:{season ?? 0}:{episode ?? 0}";
            if (_cache.TryGetValue<IReadOnlyList<WyzieSubtitleTrack>>(cacheKey, out var hit) && hit != null)
            {
                return hit;
            }

            var qs = $"?id={Uri.EscapeDataString(imdbId)}";
            if (season.HasValue)  qs += $"&season={season.Value}";
            if (episode.HasValue) qs += $"&episode={episode.Value}";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var client = _clientFactory.CreateClient();
                var raw = await client.GetStringAsync(SearchBase + qs, cts.Token);
                var parsed = ParseList(raw);
                _cache.Set(cacheKey, parsed, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ListCacheTtl });
                return parsed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Wyzie subtitle search failed ({Imdb} s{S}e{E}).", imdbId, season, episode);
                return [];
            }
        }

        public async Task<string> FetchAsVttAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            // Hard-cap to Wyzie / OpenSubtitles-style URLs so the proxy
            // can't be turned into a generic SSRF tool. Both come back
            // through Wyzie's search anyway, so a fixed allowlist is fine.
            if (!IsAllowedSubtitleHost(url))
            {
                _logger.LogWarning("Wyzie subtitle proxy refused host: {Host}", SafeHost(url));
                return null;
            }

            var cacheKey = $"wyzie:vtt:{url}";
            if (_cache.TryGetValue<string>(cacheKey, out var hit) && hit != null)
            {
                return hit;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var client = _clientFactory.CreateClient();
                var bytes = await client.GetByteArrayAsync(url, cts.Token);
                var text = DecodeText(bytes);
                var vtt = text.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                    ? text
                    : SrtToVtt(text);
                _cache.Set(cacheKey, vtt, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = VttCacheTtl });
                return vtt;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Wyzie subtitle fetch failed ({Url}).", SafeHost(url));
                return null;
            }
        }

        /// <summary>
        /// Parses Wyzie's search response and projects each track into our
        /// proxied URL shape. Dedupes by ISO language code so the captions
        /// menu doesn't show five "English" entries from different
        /// sources — Wyzie returns multiple per language ranked by their
        /// own quality signal, so taking the first per language is the
        /// "best track per language" pick.
        /// </summary>
        private IReadOnlyList<WyzieSubtitleTrack> ParseList(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            dynamic root = DeserializeObject<dynamic>(json);
            if (root == null) return [];

            // Wyzie's response shape: top-level array of track objects.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var picked = new List<WyzieSubtitleTrack>();
            var entries = new List<(string Lang, string Display, string Url)>();
            foreach (var t in root)
            {
                var url = (string)t.url;
                if (string.IsNullOrWhiteSpace(url)) continue;
                var langCode = ((string)t.language ?? string.Empty).ToLowerInvariant().Trim();
                var display  = (string)t.display ?? langCode;
                if (string.IsNullOrEmpty(langCode)) continue;
                entries.Add((langCode, display, url));
            }

            // Walk preferred languages first so the captions menu reads
            // in a sensible order; trailing tail picks up everything
            // else Wyzie returned (so users with rarer language needs
            // still get options, just below the headline set).
            foreach (var pref in PreferredLanguages)
            {
                foreach (var e in entries)
                {
                    if (string.Equals(e.Lang, pref, StringComparison.OrdinalIgnoreCase) && seen.Add(e.Lang))
                    {
                        picked.Add(new WyzieSubtitleTrack(e.Lang, e.Display, ProxyUrl(e.Url)));
                        break;
                    }
                }
            }
            foreach (var e in entries)
            {
                if (seen.Add(e.Lang))
                {
                    picked.Add(new WyzieSubtitleTrack(e.Lang, e.Display, ProxyUrl(e.Url)));
                }
            }
            return picked;
        }

        private static string ProxyUrl(string upstreamUrl)
        {
            // Relative /anime/subtitle?url=... — same-origin as the host
            // page so the <track> tag loads without needing a CORS opt-in
            // on the <video> element. Browsers resolve the relative path
            // against the document origin automatically.
            var encoded = Uri.EscapeDataString(upstreamUrl);
            return $"/anime/subtitle?url={encoded}";
        }

        private static bool IsAllowedSubtitleHost(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            var host = u.Host.ToLowerInvariant();
            return host.EndsWith("wyzie.ru")
                || host.EndsWith("opensubtitles.org")
                || host.EndsWith("opensubtitles.com")
                || host.EndsWith("subdl.com");
        }

        private static string SafeHost(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "<invalid>";
        }

        private static string DecodeText(byte[] bytes)
        {
            // Most providers return UTF-8 but a handful still emit
            // Windows-1252 / Latin-1 for older releases. Try UTF-8 strict
            // first; on failure fall back to Latin-1 which can decode any
            // byte sequence without throwing.
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

        // SRT → VTT differs mainly in:
        //   • a "WEBVTT" header line
        //   • timestamps use "." instead of "," as the millisecond separator
        // The cue text itself is copied verbatim. Lenient browsers (Chrome)
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
