using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Sockets;
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
        private readonly ILogger<AddonStreamService> _logger;

        private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan StreamsTimeout = TimeSpan.FromSeconds(12);

        // Quality detection follows the de-facto Torrentio convention every
        // well-known stream addon adopted: "1080p" / "4k" / "720p" tokens
        // in the name or description. Used as a DISPLAY label only —
        // streams whose quality we can't detect still pass through with
        // Quality = null so the source picker matches what the addon
        // itself returned in Stremio (filtering belongs upstream, in the
        // addon's own config URL).
        private static readonly (string Needle, string Quality)[] QualityNeedles =
        [
            ("2160p", "2160p"),
            ("4k",    "2160p"),
            ("1440p", "1440p"),
            ("1080p", "1080p"),
            ("720p",  "720p"),
            ("480p",  "480p"),
        ];

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

        // Narrower regex than NonBrowserCodecRegex above: just the
        // codecs that trigger the HEVC badge on the client (the
        // Chromium-desktop hardware-decode corruption case). AV1 and
        // bare "mkv"/"matroska" are intentionally excluded — AV1
        // plays cleanly in Chrome desktop, and MKV is a container
        // signal handled by the IsBrowserPlayable check, not a
        // codec one. Hi10P (10-bit AVC) is grouped with HEVC here
        // because browser AVC decoders are 8-bit-only — same
        // user-visible failure mode, same external-player advice.
        private static readonly Regex HevcOrHi10Regex = new(
            @"\b(?:hevc|x265|h\.?265|hi10p?|10[\s-]?bit)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Source / release-type tokens. Listed most-specific first so
        // "1080p BluRay REMUX" surfaces as REMUX (the better signal)
        // rather than BluRay. BDRip / BRRip collapse into BluRay
        // because the upstream-quality distinction those imply is
        // already captured by the size + bitrate the user can see.
        private static readonly (Regex Re, string Label)[] SourceDetectors =
        {
            (new Regex(@"\bREMUX\b",                       RegexOptions.Compiled | RegexOptions.IgnoreCase), "REMUX"),
            (new Regex(@"\b(?:BluRay|Blu-?Ray|BDRip|BRRip)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "BluRay"),
            (new Regex(@"\b(?:WEB[-_. ]?DL)\b",             RegexOptions.Compiled | RegexOptions.IgnoreCase), "WEB-DL"),
            (new Regex(@"\b(?:WEB[-_. ]?Rip)\b",            RegexOptions.Compiled | RegexOptions.IgnoreCase), "WEBRip"),
            (new Regex(@"\b(?:HDTV|HDRip)\b",               RegexOptions.Compiled | RegexOptions.IgnoreCase), "HDTV"),
            (new Regex(@"\bDVDRip\b",                       RegexOptions.Compiled | RegexOptions.IgnoreCase), "DVD"),
        };

        // HDR / Dolby Vision detection. DV / HDR10+ first so a
        // "Dolby Vision HDR10+" release reports the higher tier
        // before the more common HDR10 / HDR fallbacks. Plain
        // "10bit" is intentionally NOT here — it's a colour-depth
        // signal, not an HDR signal (Hi10P AVC is 10-bit but SDR),
        // and the HEVC badge already flags the browser-compat
        // implication.
        private static readonly (Regex Re, string Label)[] HdrDetectors =
        {
            (new Regex(@"\b(?:Dolby[\s.]*Vision|DoVi|DV)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "DV"),
            (new Regex(@"\bHDR10\+",                         RegexOptions.Compiled | RegexOptions.IgnoreCase), "HDR10+"),
            (new Regex(@"\bHDR10\b",                         RegexOptions.Compiled | RegexOptions.IgnoreCase), "HDR10"),
            (new Regex(@"\bHDR\b",                           RegexOptions.Compiled | RegexOptions.IgnoreCase), "HDR"),
        };

        // Audio codec + channel layout in a single match. The codec
        // alternation is most-specific first (DDP / EAC3 → DD / AC3,
        // DTS-HD MA → DTS) so a "DDP5.1 Atmos" tag doesn't collapse
        // to bare "DD". The optional trailing channel pattern eats
        // "5.1" / "7.1" / "2.0" / "5.1ch" so the badge reads as a
        // full audio spec ("DDP5.1") rather than a bare codec.
        //
        // Note: no \b between the codec and the channel group —
        // regex word-boundary doesn't fire between letters and
        // digits ("DDP" → "5" is \w→\w), so requiring one there
        // would break the common "DDP5.1" / "AAC2.0" packing.
        private static readonly Regex AudioRegex = new(
            @"\b(Atmos|TrueHD|DTS-?HD(?:[\s.]?MA)?|DTS-?X|DDP|E-?AC-?3|EAC3|AC-?3|DD\+|DTS|FLAC|AAC|Opus|MP3|PCM|Vorbis)(?:[\s.]?(\d\.\d)(?:ch)?)?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public AddonStreamService(
            IHttpClientFactory clientFactory,
            ILogger<AddonStreamService> logger)
        {
            _clientFactory = clientFactory;
            _logger = logger;
        }

        // ── SSRF guard ──────────────────────────────────────────────────────
        // The manifest URL (and the addon root derived from it) is user-supplied and
        // fetched server-side, so without a check a user could point AniSync at an
        // internal service or the cloud metadata endpoint (169.254.169.254) and read
        // the response back. Resolve the host and refuse if any resolved address is
        // loopback / private / link-local / unique-local. A small TOCTOU window remains
        // (HttpClient re-resolves on connect), but this blocks the direct-literal and
        // resolves-to-internal cases the audit called out; it's re-checked on the runtime
        // stream fetch too, so a host that later rebinds to a private IP is also caught.
        private static async Task<bool> IsSafeOutboundUrlAsync(string url, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            try
            {
                var host = uri.DnsSafeHost;
                var addresses = IPAddress.TryParse(host, out var literal)
                    ? new[] { literal }
                    : await Dns.GetHostAddressesAsync(host, ct);
                if (addresses.Length == 0) return false;
                foreach (var ip in addresses)
                    if (IsDisallowedAddress(ip)) return false;
                return true;
            }
            catch
            {
                // DNS failure / malformed host → treat as unsafe rather than fetch blind.
                return false;
            }
        }

        private static bool IsDisallowedAddress(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;                  // 127.0.0.0/8, ::1
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return b[0] == 0                                        // 0.0.0.0/8
                    || b[0] == 10                                       // 10/8
                    || b[0] == 127                                      // loopback
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)       // 100.64/10 CGNAT
                    || (b[0] == 169 && b[1] == 254)                     // 169.254/16 (incl. metadata)
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)        // 172.16/12
                    || (b[0] == 192 && b[1] == 168);                    // 192.168/16
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return ip.IsIPv6LinkLocal                               // fe80::/10
                    || ip.IsIPv6SiteLocal                              // fec0::/10
                    || (b[0] & 0xFE) == 0xFC                            // fc00::/7 unique-local
                    || b[0] == 0x00;                                    // ::/8 (unspecified / reserved)
            }
            return true; // unknown address family → refuse
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
            if (!await IsSafeOutboundUrlAsync(trimmed, ct))
            {
                _logger.LogWarning("Refused addon manifest fetch for {Url}: host resolves to a disallowed (loopback/private/link-local) address.", trimmed);
                return null;
            }

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
            AnimeService primaryService,
            string clientIp = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl) || links == null)
            {
                return [];
            }

            var addonRoot = ExtractAddonRoot(manifestUrl);
            if (addonRoot == null) return [];

            var stremioId = BuildStremioId(links, season, episode, primaryService);
            if (stremioId == null) return [];
            var idType = stremioId.Value.Type;
            var idPath = stremioId.Value.Path;

            // No server-side cache: stream URLs are token-bound to the
            // requesting IP (MediaFusion / Real-Debrid signed paths) so
            // a shared per-process cache would hand user B a URL minted
            // for user A's IP. The watch page only re-hits this when the
            // user navigates back to the episode, so the duplicate-fetch
            // window is small in practice.
            var streamsUrl = $"{addonRoot}/stream/{idType}/{idPath}.json";

            // SSRF re-check at fetch time: the addon root was validated when the user added
            // it, but DNS can rebind to an internal address after the fact.
            if (!await IsSafeOutboundUrlAsync(streamsUrl, ct))
            {
                _logger.LogWarning("Refused addon stream fetch via {Host}: host resolves to a disallowed address.", addonRoot);
                return [];
            }

            // Single retry against the addon when the first attempt
            // returns empty / errors. Stream addons (Torrentio,
            // MediaFusion, Comet, Jackettio, …) frequently 200-empty
            // under rate-limit pressure or backend hiccups — same URL
            // refreshed twice can return a populated list once and
            // an empty list once, which lines up with user reports
            // of "sometimes I get a stream, sometimes I don't" on
            // identical URLs. A bare retry recovers the transient
            // case without needing per-addon circuit-breaker state
            // or upstream-specific heuristics. ~500 ms backoff is
            // enough for most rate-limit windows to elapse without
            // adding noticeable latency to the page render.
            const int MaxAttempts = 2;
            List<AddonStream> lastResult = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(StreamsTimeout);

                    var client = _clientFactory.CreateClient();
                    using var req = new HttpRequestMessage(HttpMethod.Get, streamsUrl);
                    // Tell the addon who the real client is so it can bind
                    // its playback URLs to the user's IP rather than ours.
                    // X-Forwarded-For is the universally-recognised header
                    // — sending the CDN-specific variants alongside it
                    // (CF-Connecting-IP / Fly-Client-IP) tends to trip
                    // anti-spoofing checks on addons that expect those
                    // headers only from their own edge, so we stick to
                    // X-Forwarded-For exclusively.
                    if (!string.IsNullOrEmpty(clientIp))
                    {
                        req.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
                    }
                    var response = await client.SendAsync(req, cts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "Addon stream fetch {Status} for {Path} via {Host} (clientIp={Ip}, attempt={Attempt}).",
                            (int)response.StatusCode, idPath, addonRoot,
                            string.IsNullOrEmpty(clientIp) ? "(none)" : clientIp, attempt);
                        lastResult = [];
                    }
                    else
                    {
                        var content = await response.Content.ReadAsStringAsync(cts.Token);
                        lastResult = ParseStreams(content, addonRoot);
                        if (lastResult.Count > 0)
                        {
                            _logger.LogInformation(
                                "Addon stream fetch returned {Count} entries for {Path} via {Host} (clientIp={Ip}, attempt={Attempt}).",
                                lastResult.Count, idPath, addonRoot,
                                string.IsNullOrEmpty(clientIp) ? "(none)" : clientIp, attempt);
                            return lastResult;
                        }
                        _logger.LogInformation(
                            "Addon stream fetch returned 0 entries for {Path} via {Host} (clientIp={Ip}, attempt={Attempt}).",
                            idPath, addonRoot,
                            string.IsNullOrEmpty(clientIp) ? "(none)" : clientIp, attempt);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Addon stream fetch timed out ({Path} via {Host}, attempt={Attempt}).",
                        idPath, addonRoot, attempt);
                    lastResult = [];
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Addon stream fetch failed ({Path} via {Host}, attempt={Attempt}).",
                        idPath, addonRoot, attempt);
                    lastResult = [];
                }

                if (attempt < MaxAttempts)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
                    catch (OperationCanceledException) { return lastResult ?? []; }
                }
            }
            return lastResult ?? [];
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
        /// (anime catalog variant) → caller's primary-tracker id
        /// (<c>mal:N</c> / <c>anilist:N</c>) as a last-resort fallback
        /// for entries that have no IMDb / Kitsu mapping in our local
        /// data (very new shows, anime with sparse cross-service rows).
        /// Returns null when none of those resolve, or when an episode
        /// lookup is requested without season/episode info.
        /// </summary>
        private static (string Type, string Path)? BuildStremioId(
            AnimeSourceLinks links,
            int? season,
            int? episode,
            AnimeService primaryService)
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
                var primaryPath = BuildPrimaryServicePath(links, primaryService, episode.Value);
                if (primaryPath != null)
                {
                    return ("series", primaryPath);
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
            var primaryMoviePath = BuildPrimaryServicePath(links, primaryService, episode: null);
            if (primaryMoviePath != null)
            {
                return ("movie", primaryMoviePath);
            }
            return null;
        }

        /// <summary>
        /// Builds the <c>{prefix}:{id}</c> (movie) or <c>{prefix}:{id}:{ep}</c>
        /// (series) shape for the user's primary-tracker id-space when
        /// neither IMDb nor Kitsu resolved. Kitsu is intentionally not
        /// handled here — the caller already covered that branch with the
        /// dedicated <c>kitsu:</c> fallback, so a Kitsu primary with a
        /// missing KitsuId genuinely has no third id to try.
        /// </summary>
        private static string BuildPrimaryServicePath(AnimeSourceLinks links, AnimeService primaryService, int? episode)
        {
            int? id;
            string prefix;
            switch (primaryService)
            {
                case AnimeService.MyAnimeList:
                    id = links.MalId;
                    prefix = "mal";
                    break;
                case AnimeService.Anilist:
                    id = links.AnilistId;
                    prefix = "anilist";
                    break;
                default:
                    return null;
            }
            if (!id.HasValue) return null;
            return episode.HasValue
                ? $"{prefix}:{id.Value}:{episode.Value}"
                : $"{prefix}:{id.Value}";
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
                var url = NormalizeAddonStreamUrl((string)s["url"]);
                if (string.IsNullOrWhiteSpace(url)) continue;

                var name = (string)s["name"] ?? string.Empty;
                // Different addons populate different descriptive
                // fields — Torrentio uses `title`, MediaFusion uses
                // `description` (and may also use `title`). Feed all
                // three to the detection helpers so the regex hits
                // size / seeders / language tokens regardless of which
                // field carries them.
                var rawDescription = (string)s["description"] ?? string.Empty;
                var rawTitle = (string)s["title"] ?? string.Empty;
                var combined = $"{name}\n{rawDescription}\n{rawTitle}";

                var quality = DetectQuality(combined);

                var size = DetectSize(combined);
                var seeders = DetectSeeders(combined);
                var language = DetectLanguage(combined);
                var playable = IsBrowserPlayable(url, combined);
                // Detected from the full haystack (name + description
                // + title) so we catch MediaFusion's codec line which
                // lives in `description` and gets dropped before the
                // JSON projection reaches the client.
                var isHevc = HevcOrHi10Regex.IsMatch(combined);
                // Comet emits these on its multi-line description
                // (BluRay / WEB-DL, HDR / DV, DDP5.1 / Atmos). They
                // also land in the filename for releases scraped by
                // Torrentio / MediaFusion, so the same haystack
                // surfaces them across every addon shape.
                var source = DetectSource(combined);
                var hdr = DetectHdr(combined);
                var audio = DetectAudio(combined);

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

                // Pick a single human-readable release-name line for
                // display. Torrentio puts the filename straight in
                // `title`; MediaFusion shoves it into a multi-line
                // `description` after a codec line (and sometimes tags
                // it with 📁). Walking all three fields with the same
                // heuristic — explicit 📁 marker first, then the first
                // line that isn't pure metadata — keeps the source
                // picker from showing "hevc" / "WEB AAC" as the title.
                var displayTitle = PickReleaseTitle(rawDescription, rawTitle, name);

                raw.Add(new AddonStream(
                    Name: name,
                    Title: displayTitle,
                    Url: url,
                    Quality: quality,
                    Size: size,
                    Playable: playable,
                    BingeGroup: bingeGroup,
                    Seeders: seeders,
                    Language: language,
                    InfoHash: infoHash,
                    FileIdx: fileIdx,
                    Provider: providerFallback,
                    IsHevc: isHevc,
                    Source: source,
                    Hdr: hdr,
                    Audio: audio));
            }

            // Return in the addon's emit order — no re-sort, no cap. Each
            // addon's config URL is where the user already expressed their
            // filtering / ordering preferences (Torrentio's qualityfilter=,
            // sort=, perpage=); second-guessing that here is what made
            // MediaFusion's single-result responses for some episodes
            // collapse to zero rows on AniSync vs. the same result
            // appearing in Stremio.
            return raw;
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

        private static string DetectSource(string haystack)
        {
            foreach (var (re, label) in SourceDetectors)
            {
                if (re.IsMatch(haystack)) return label;
            }
            return null;
        }

        private static string DetectHdr(string haystack)
        {
            foreach (var (re, label) in HdrDetectors)
            {
                if (re.IsMatch(haystack)) return label;
            }
            return null;
        }

        private static string DetectAudio(string haystack)
        {
            var m = AudioRegex.Match(haystack);
            if (!m.Success) return null;
            // Collapse runs of whitespace to a single space — "DTS-HD MA  5.1"
            // → "DTS-HD MA 5.1" — but keep the single space between codec
            // and channel info intact for readability. "DDP5.1" stays packed.
            return Regex.Replace(m.Value.Trim(), @"\s+", " ");
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

        /// <summary>
        /// Walks a URL down one URL-decode level at a time, stopping
        /// at the "single-encoded" canonical form. Workaround for
        /// MediaFusion configurations that return playback URLs with
        /// filenames encoded multiple times (e.g. <c>%25255B</c> for
        /// <c>[</c> and <c>%252520</c> for a space) — the server then
        /// 403s on its own URL because the signed token in the path
        /// is computed against the once-encoded form. Guarded against
        /// over-decoding a legitimate literal <c>%</c> by checking
        /// that each decoded result still contains another <c>%XX</c>
        /// percent sequence before accepting it.
        /// </summary>
        private static string NormalizeAddonStreamUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var safety = 4;
            while (safety-- > 0)
            {
                if (url.IndexOf("%25", StringComparison.OrdinalIgnoreCase) < 0) break;
                string decoded;
                try { decoded = Uri.UnescapeDataString(url); }
                catch { break; }
                if (string.Equals(decoded, url, StringComparison.Ordinal)) break;
                // Stop if the decoded form is no longer URL-encoded —
                // protects against decoding a single %25 that meant a
                // literal % in the path. We only accept the decoded
                // form when it still has at least one %XX pair.
                if (!Regex.IsMatch(decoded, "%[0-9A-Fa-f]{2}")) break;
                url = decoded;
            }
            return url;
        }

        /// <summary>
        /// Pure metadata lines like <c>"🎞️ hevc 🎵 AAC"</c> or
        /// <c>"📺 WEB 🎞️ av1"</c> consist exclusively of emoji
        /// indicators and a fixed set of codec / container / quality
        /// tokens. We skip such lines when looking for the release-
        /// name line to display.
        /// </summary>
        private static readonly Regex MetadataTokenRegex = new(
            @"^(?:hevc|x265|x264|h\.?26[45]|av1|web(?:-?dl)?|aac|ac3|dts|flac|opus|mp3|mp4|mkv|webm|10[\s-]?bit|hdr|bluray|brrip|webrip|hd|sd|\d{3,4}p|4k|multi|dual|sub(?:bed)?|dub(?:bed)?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TokenSplitterRegex = new(
            @"[\s|+/,]+",
            RegexOptions.Compiled);

        /// <summary>
        /// Strips leading/trailing non-letter/non-digit chars so a
        /// token like <c>"🎵AAC"</c> reduces to <c>"AAC"</c> for the
        /// metadata-token check.
        /// </summary>
        private static readonly Regex EmojiTrimRegex = new(
            @"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$",
            RegexOptions.Compiled);

        private static bool IsMetadataOnlyLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            var tokens = TokenSplitterRegex.Split(line);
            var hasContent = false;
            foreach (var t in tokens)
            {
                var trimmed = t.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                var stripped = EmojiTrimRegex.Replace(trimmed, "");
                if (string.IsNullOrEmpty(stripped)) continue; // emoji-only token
                hasContent = true;
                if (!MetadataTokenRegex.IsMatch(stripped)) return false;
            }
            return hasContent;
        }

        // Lines that start with a metadata-tag emoji (size, seeders,
        // language flags, source-link) carry only that one piece of
        // info — never the release name itself — so we skip them
        // outright without doing the token-by-token check.
        private static readonly Regex MetadataPrefixedLineRegex = new(
            @"^[\s💾👤🌐🔗⭐]",
            RegexOptions.Compiled);

        /// <summary>
        /// Picks a display-friendly release-name line from the
        /// supplied sources, in order. Walks the lines of each source
        /// twice: first looking for a 📁-tagged filename line (the
        /// strongest signal — emitted by MediaFusion and similar
        /// addons), then for the first line that doesn't look like
        /// pure codec / size / seeders metadata. Falls back to the
        /// first non-empty line if no source yields a clean release-
        /// name. Keeps the source picker from showing
        /// <c>"hevc"</c> / <c>"WEB AAC"</c> as the title when the
        /// real filename is one line down.
        /// </summary>
        private static string PickReleaseTitle(params string[] sources)
        {
            foreach (var src in sources)
            {
                if (string.IsNullOrEmpty(src)) continue;
                // Pass 1: explicit 📁 filename marker.
                foreach (var raw in src.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("📁"))
                    {
                        var stripped = line["📁".Length..].Trim();
                        if (!string.IsNullOrEmpty(stripped)) return stripped;
                    }
                }
                // Pass 2: first line that isn't pure metadata.
                foreach (var raw in src.Split('\n'))
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (MetadataPrefixedLineRegex.IsMatch(line)) continue;
                    if (IsMetadataOnlyLine(line)) continue;
                    return line;
                }
            }
            // Pass 3: first non-empty line from any source.
            foreach (var src in sources)
            {
                if (string.IsNullOrEmpty(src)) continue;
                foreach (var raw in src.Split('\n'))
                {
                    var trimmed = raw.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) return trimmed;
                }
            }
            return string.Empty;
        }
    }
}
