using AnimeList.Models.Api;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Public (no per-user auth) watch-page proxies — the header-less twins of the
    /// MVC <c>MetaController</c> subtitle + resolve-stream surface, which is filtered
    /// out of the web head. Neither endpoint touches user state:
    /// <list type="bullet">
    /// <item><b>Subtitle proxy</b> — fetches an upstream OpenSubtitles URL through our
    /// origin and converts SRT→VTT on the fly so the player's <c>&lt;track&gt;</c> stays
    /// same-origin (and Chromecast / VLC can read it). Two URL transports: a query
    /// param (<c>?url=</c>) and a base64url path segment ending in
    /// <c>/subtitle.vtt</c> (so VLC's extension sniff registers it as a subtitle).</item>
    /// <item><b>Resolve-stream</b> — a host-allow-listed 302 follower that resolves a
    /// resolver URL (Torrentio's <c>strem.fun/resolve/…</c> etc.) to the post-redirect
    /// debrid CDN URL, which AniSync's server can fetch where a Cloudflare Worker
    /// can't.</item>
    /// </list>
    /// Added to <c>ApiOnlyControllerProvider</c>'s allow-list so the web head exposes it.
    /// </summary>
    [ApiController]
    [Route("api/v1")]
    [EnableRateLimiting("api")]
    [Tags("Watch proxies")]
    public class MetaProxyController : ControllerBase
    {
        private readonly ISubtitleService _subtitleService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MetaProxyController> _logger;

        public MetaProxyController(
            ISubtitleService subtitleService,
            IHttpClientFactory httpClientFactory,
            ILogger<MetaProxyController> logger)
        {
            _subtitleService = subtitleService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Proxies a subtitle URL through our origin and converts SRT to VTT on the
        /// fly. Backs the &lt;track src&gt; tag on the watch page — without this hop the
        /// &lt;track&gt; load would be cross-origin. Both <c>/subtitle</c> and
        /// <c>/subtitle.vtt</c> resolve here. Ported from MetaController.Subtitle.
        /// </summary>
        [HttpGet("subtitle")]
        [HttpGet("subtitle.vtt")]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> Subtitle(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return BadRequest();
            return await ServeSubtitleVtt(url);
        }

        /// <summary>
        /// Alternative subtitle proxy that carries the upstream URL as a base64url
        /// path segment ending in a literal <c>/subtitle.vtt</c>. Used by the watch
        /// page's external-launch flow: VLC's subtitle-format detection looks at the
        /// URL's file extension, and a trailing <c>?url=…</c> means the slave never
        /// registers as a <c>.vtt</c>. Ported from MetaController.SubtitleByPath.
        /// </summary>
        [HttpGet("sub/{encoded}/subtitle.vtt")]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> SubtitleByPath(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded)) return BadRequest();
            string upstream;
            try
            {
                // base64url ↔ base64: undo the URL-safe substitutions and restore
                // padding so the .NET decoder accepts it.
                var b64 = encoded.Replace('-', '+').Replace('_', '/');
                var pad = b64.Length % 4;
                if (pad > 0) b64 += new string('=', 4 - pad);
                upstream = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            catch
            {
                return BadRequest();
            }
            if (string.IsNullOrWhiteSpace(upstream)) return BadRequest();
            return await ServeSubtitleVtt(upstream);
        }

        private async Task<IActionResult> ServeSubtitleVtt(string url)
        {
            var vtt = await _subtitleService.FetchAsVttAsync(url);
            if (string.IsNullOrEmpty(vtt))
                return StatusCode(502);
            // 1-hour client cache so re-seeks / re-renders don't refetch.
            Response.Headers["Cache-Control"] = "public, max-age=3600";
            // Allow cross-origin reads so Chromecast's Default Media Receiver (a
            // separate gstatic.com origin) can fetch the VTT when the user casts.
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            // Suggest a sensible filename for external players that sniff
            // Content-Disposition (VLC does when the URL path doesn't end in a known
            // subtitle extension).
            Response.Headers["Content-Disposition"] = "inline; filename=\"subtitle.vtt\"";
            return Content(vtt, "text/vtt", System.Text.Encoding.UTF8);
        }

        /// <summary>
        /// Resolves a resolver URL (Torrentio's <c>strem.fun/resolve/…</c> etc.) to the
        /// post-redirect debrid CDN URL its 302 points at. Host-allow-listed so this
        /// isn't a generic redirect-follower someone could aim at internal addresses.
        /// Ported from MetaController.ResolveStream.
        /// </summary>
        [HttpGet("resolve-stream")]
        [ProducesResponseType(typeof(ResolveStreamResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ResolveStream(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                return BadRequest(new ApiError("invalid url"));
            }
            // Lock to known resolver / CDN hosts.
            var host = u.Host.ToLowerInvariant();
            var allowed = host.EndsWith("strem.fun")
                || host.EndsWith("real-debrid.com")
                || host.EndsWith("alldebrid.com")
                || host.EndsWith("debrid-link.com")
                || host.EndsWith("premiumize.me")
                || host.EndsWith("torbox.app")
                || host.EndsWith("offcloud.com")
                // Stremio stream-addon hosts whose /playback URLs resolve through their
                // own infrastructure (MediaFusion's ElfHosted instance is the most
                // common; the broader .elfhosted.com cover catches Comet etc.).
                || host.EndsWith("mediafusion.elfhosted.com")
                || host.EndsWith(".elfhosted.com");
            if (!allowed)
                return Forbid();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                var client = _httpClientFactory.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                // Range to keep the body tiny — we only care about the post-redirect URI.
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                // Most CF-protected resolvers reject obvious bot UAs; a plausible browser
                // UA gets us through (doing what the user's browser would do anyway).
                req.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");

                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                // RequestMessage.RequestUri reflects the URL of the *last* request the
                // HttpClient made — i.e. after all redirects (AllowAutoRedirect default).
                // Falls back to the original on any error.
                var finalUrl = res.RequestMessage?.RequestUri?.ToString() ?? url;
                return new JsonResult(new ResolveStreamResponse(finalUrl));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ResolveStream failed for {Url}.", url);
                return new JsonResult(new ResolveStreamResponse(url));
            }
        }
    }
}
