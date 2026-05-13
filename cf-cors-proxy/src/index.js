// AniSync CORS proxy — Cloudflare Worker.
//
// Re-emits responses from debrid CDNs (real-debrid.com, alldebrid.com, etc.)
// with the missing Access-Control-Allow-Origin header so the browser-side
// matroska-subtitles extractor can stream MKV bytes without the browser
// blocking the response.
//
// Hostnames are restricted via ALLOWED_HOST_SUFFIXES below so the worker
// can't be used as a generic open proxy / SSRF tool. An optional
// PROXY_SECRET (set as a Worker secret) gates access — set the matching
// CORS_PROXY_SECRET on AniSync to use it.

const ALLOWED_HOST_SUFFIXES = [
    // Debrid CDNs the AniSync watch page hits via Torrentio. Matches
    // ANY subdomain — real-debrid.com handles `*.download.real-debrid.com`
    // / `*.real-debrid.com` etc. as a single allowlist entry.
    'real-debrid.com',
    'alldebrid.com',
    'debrid-link.com',
    'premiumize.me',
    'torbox.app',
    'offcloud.com',
    // Stremio's Torrentio resolver (redirects to one of the debrid CDNs).
    'strem.fun',
];

// Browser-controlled request headers we forward upstream. Range is the
// critical one — matroska-subtitles uses Range requests to seek to
// individual cue offsets without downloading the whole file.
const FORWARDED_REQUEST_HEADERS = ['range', 'accept', 'accept-encoding'];

// Response headers we copy back. Content-Range + Accept-Ranges keep
// Range semantics working end-to-end; Content-Type so the browser
// dispatches correctly; Last-Modified / ETag for any future cache layer.
const FORWARDED_RESPONSE_HEADERS = [
    'content-type',
    'content-length',
    'content-range',
    'accept-ranges',
    'last-modified',
    'etag',
];

function corsHeaders(extra) {
    const h = {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Methods': 'GET, HEAD, OPTIONS',
        'Access-Control-Allow-Headers': 'Range, Content-Type',
        // Without explicit Expose-Headers the browser won't surface
        // Content-Length / Content-Range to JS even though the
        // headers are physically present.
        'Access-Control-Expose-Headers': 'Content-Length, Content-Range, Accept-Ranges',
        'Access-Control-Max-Age': '86400',
    };
    if (extra) Object.assign(h, extra);
    return h;
}

function hostAllowed(hostname) {
    const h = hostname.toLowerCase();
    return ALLOWED_HOST_SUFFIXES.some(s => h === s || h.endsWith('.' + s));
}

function errorResponse(message, status) {
    return new Response(message, { status, headers: corsHeaders() });
}

export default {
    async fetch(request, env) {
        // CORS preflight — browsers send OPTIONS before any fetch
        // carrying a Range header. Answer 204 + headers and we're done.
        if (request.method === 'OPTIONS') {
            return new Response(null, { status: 204, headers: corsHeaders() });
        }

        if (request.method !== 'GET' && request.method !== 'HEAD') {
            return errorResponse('Method not allowed', 405);
        }

        const url = new URL(request.url);
        const target = url.searchParams.get('url');
        if (!target) {
            return errorResponse('Missing ?url parameter', 400);
        }

        // Optional shared secret. When set, callers must include
        // &secret=<value>. Cheap protection against random scanners
        // finding the worker URL and using it as an open proxy.
        if (env.PROXY_SECRET) {
            const provided = url.searchParams.get('secret');
            if (provided !== env.PROXY_SECRET) {
                return errorResponse('Unauthorized', 401);
            }
        }

        let parsed;
        try {
            parsed = new URL(target);
        } catch (_) {
            return errorResponse('Invalid target URL', 400);
        }

        if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') {
            return errorResponse('Only http(s) URLs allowed', 400);
        }

        if (!hostAllowed(parsed.hostname)) {
            return errorResponse(`Host not allowed: ${parsed.hostname}`, 403);
        }

        const upstreamHeaders = new Headers();
        for (const h of FORWARDED_REQUEST_HEADERS) {
            const v = request.headers.get(h);
            if (v) upstreamHeaders.set(h, v);
        }
        // Some CDNs serve different bytes (or 403s) based on UA. Set
        // a generic browser UA so we don't look like a bot.
        if (!upstreamHeaders.has('user-agent')) {
            upstreamHeaders.set(
                'user-agent',
                'Mozilla/5.0 (compatible; AniSync-CorsProxy/1.0)'
            );
        }

        let upstreamRes;
        try {
            upstreamRes = await fetch(parsed.toString(), {
                method: request.method,
                headers: upstreamHeaders,
                // RD URLs redirect to actual file CDNs — follow so the
                // caller sees the final bytes.
                redirect: 'follow',
            });
        } catch (e) {
            return errorResponse(`Upstream fetch failed: ${e.message}`, 502);
        }

        const responseHeaders = corsHeaders();
        for (const h of FORWARDED_RESPONSE_HEADERS) {
            const v = upstreamRes.headers.get(h);
            if (v) responseHeaders[h] = v;
        }

        // Stream the body — Cloudflare Workers supports passing the
        // upstream Response.body straight through, so the entire file
        // doesn't have to land in worker memory at once.
        return new Response(upstreamRes.body, {
            status: upstreamRes.status,
            statusText: upstreamRes.statusText,
            headers: responseHeaders,
        });
    },
};
