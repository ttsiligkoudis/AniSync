// AniSync MKV subtitle extractor — Cloudflare Worker.
//
// Range-fetches a debrid-CDN MKV URL, walks its EBML index, and
// returns the embedded SSA / ASS / SRT tracks as JSON. ~10-30 MB
// network instead of the ~700 MB the streaming matroska-subtitles
// approach would pull through cf-cors-proxy.
//
// Companion to cf-cors-proxy/ rather than a replacement — the proxy
// still serves the (smaller, but harder to avoid) embedded-extract
// path for files without a SeekHead / Cues index. AniSync's client
// is wired to try this Worker first and fall back to the
// proxy + matroska-subtitles pipeline when this Worker returns 404
// (no index found) or 502 (parse failure).
//
// Hosts are locked down via ALLOWED_HOST_SUFFIXES so the worker
// can't be used as a generic open MKV extractor.

import { RangeReader } from './reader.js';
import { extractSubtitles } from './mkv.js';

const ALLOWED_HOST_SUFFIXES = [
    'real-debrid.com',
    'alldebrid.com',
    'debrid-link.com',
    'premiumize.me',
    'torbox.app',
    'offcloud.com',
];

function corsHeaders(extra) {
    const h = {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Methods': 'GET, OPTIONS',
        'Access-Control-Allow-Headers': 'Content-Type',
        'Access-Control-Max-Age': '86400',
        'Content-Type': 'application/json; charset=utf-8',
    };
    if (extra) Object.assign(h, extra);
    return h;
}

function jsonResponse(status, body) {
    return new Response(JSON.stringify(body), { status, headers: corsHeaders() });
}

function hostAllowed(hostname) {
    const h = hostname.toLowerCase();
    return ALLOWED_HOST_SUFFIXES.some(s => h === s || h.endsWith('.' + s));
}

export default {
    async fetch(request, env) {
        if (request.method === 'OPTIONS') {
            return new Response(null, { status: 204, headers: corsHeaders() });
        }
        if (request.method !== 'GET') {
            return jsonResponse(405, { error: 'method not allowed' });
        }

        const url = new URL(request.url);
        const target = url.searchParams.get('url');
        if (!target) return jsonResponse(400, { error: 'missing ?url parameter' });

        if (env.PROXY_SECRET) {
            const provided = url.searchParams.get('secret');
            if (provided !== env.PROXY_SECRET) {
                return jsonResponse(401, { error: 'unauthorized' });
            }
        }

        let parsed;
        try { parsed = new URL(target); }
        catch (_) { return jsonResponse(400, { error: 'invalid target URL' }); }
        if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') {
            return jsonResponse(400, { error: 'only http(s) URLs allowed' });
        }
        if (!hostAllowed(parsed.hostname)) {
            return jsonResponse(403, { error: `host not allowed: ${parsed.hostname}` });
        }

        const reader = new RangeReader(target);
        try {
            const result = await extractSubtitles(reader);
            return new Response(JSON.stringify({
                tracks: result.tracks,
                stats: {
                    fileSize: reader.totalSize,
                    trackCount: result.tracks.length,
                },
            }), { status: 200, headers: corsHeaders() });
        } catch (e) {
            // Distinguish "this file just doesn't have an index" from
            // "something genuinely broke" so the client can decide
            // whether to fall back to the streaming proxy path.
            const msg = (e && e.message) || String(e);
            const indexless =
                msg.includes('no SeekHead') ||
                msg.includes('no Tracks pointer') ||
                msg.includes('no index');
            return jsonResponse(indexless ? 404 : 502, {
                error: msg,
                indexless,
            });
        }
    },
};
