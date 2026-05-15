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
    async fetch(request, env, ctx) {
        // Last-resort try/catch around the entire handler. The
        // inner try (around extractSubtitles) catches everything our
        // own code can throw, but unforeseen errors — sync throws
        // from URL parsing, oddball runtime exceptions, framework
        // bugs — would otherwise escape and surface as a CF 5xx,
        // which counts against the failure-rate breaker that
        // surfaces as 503 to subsequent users. Anything we can't
        // map to a clean response still returns 200 with an
        // "extracted: false" payload so the client falls back and
        // CF's monitoring stays happy.
        try {
            return await handleFetch(request, env, ctx);
        } catch (e) {
            return new Response(JSON.stringify({
                tracks: [],
                extracted: false,
                reason: 'worker_error',
                error: (e && e.message) || String(e),
            }), { status: 200, headers: corsHeaders() });
        }
    },
};

// How long a successful extraction stays cached on CF's edge. Two
// hours matches the typical RD-token validity window: re-watches of
// the same source within that window hit cache and skip RD entirely.
// After that the URL would have rotated upstream anyway so cached
// hits would 404 on the bytes.
const EXTRACT_CACHE_SECONDS = 2 * 60 * 60;

async function handleFetch(request, env, ctx) {
        if (request.method === 'OPTIONS') {
            return new Response(null, { status: 204, headers: corsHeaders() });
        }
        if (request.method !== 'GET') {
            return jsonResponse(405, { error: 'method not allowed' });
        }

        // CF Cache API check up front. The default cache keys by full
        // request URL, so identical `?url=...&secret=...&lang=...`
        // requests share a cache entry. Cache hits cost nothing — no
        // subrequest, no CPU, no upstream traffic. Only successful
        // extractions are cached (see Cache-Control header below);
        // failures fall through so we re-try on the next attempt.
        const cache = caches.default;
        const cached = await cache.match(request);
        if (cached) {
            // Tag the response so DevTools shows the cache hit
            // unambiguously — the body is unchanged otherwise.
            const headers = new Headers(cached.headers);
            headers.set('x-anisync-cache', 'hit');
            return new Response(cached.body, {
                status: cached.status,
                headers,
            });
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
            // Optional ?lang= filter.
            //   "auto"          — keep only the first track whose
            //                     language starts with "en" or whose
            //                     name contains "english"/"eng"
            //                     (mirrors the client's auto-promote
            //                     rule). Saves worker memory and
            //                     response size proportional to the
            //                     number of tracks we'd otherwise
            //                     accumulate cues for.
            //   "eng" / "spa"…  — explicit ISO-639-2/B language code,
            //                     filter to tracks matching it.
            //   omitted         — return every subtitle track (legacy
            //                     behaviour).
            const langParam = (url.searchParams.get('lang') || '').toLowerCase();
            // Sharding params. shards>1 splits the cluster-offset
            // list into N contiguous slices; each invocation processes
            // only its slice. Lets the client fan out big BD remuxes
            // across multiple Worker invocations to bypass the
            // single-invocation 128 MB memory cap (MAX_TOTAL_FETCH =
            // 120 MB inside mkv.js). Defaults to 1 = single-shot
            // behaviour. The cache key already includes these via
            // the full request URL, so each (shards, shard) pair
            // caches independently.
            const shardsParam = parseInt(url.searchParams.get('shards') || '1', 10) || 1;
            const shardParam = parseInt(url.searchParams.get('shard') || '0', 10) || 0;
            const result = await extractSubtitles(reader, {
                lang: langParam || null,
                shards: shardsParam,
                shard: shardParam,
            });
            // Build with Cache-Control so cache.put accepts it. Two
            // hours covers typical re-watch sessions; after that the
            // RD URL has rotated and any cached entry would be moot.
            const successBody = JSON.stringify({
                tracks: result.tracks,
                extracted: true,
                truncated: !!result.truncated,
                shard: result.shard,
                shards: result.shards,
                clustersTotal: result.clustersTotal,
                clustersInShard: result.clustersInShard,
                stats: {
                    fileSize: reader.totalSize,
                    trackCount: result.tracks.length,
                },
            });
            const response = new Response(successBody, {
                status: 200,
                headers: corsHeaders({
                    'Cache-Control': `public, max-age=${EXTRACT_CACHE_SECONDS}`,
                    'x-anisync-cache': 'miss',
                }),
            });
            // ctx.waitUntil lets the cache write happen after we've
            // returned to the client — zero added latency. Use
            // response.clone() because Response bodies are
            // single-consumer. Truncated responses (single-shot ran
            // out of MAX_TOTAL_FETCH budget) are NOT cached: the
            // client uses the truncated flag as the signal to retry
            // with sharding, and a cached truncated body would
            // suppress that signal for two hours.
            if (!result.truncated) {
                ctx.waitUntil(cache.put(request, response.clone()));
            }
            return response;
        } catch (e) {
            // Collapse extraction failures (indexless file, mid-stream
            // upstream drops, parse errors, memory pressure) into a
            // SUCCESSFUL 200 response with `extracted: false` and a
            // human-readable reason. The client checks the flag and
            // falls back to the cf-cors-proxy streaming path the same
            // way it would on a 5xx — but we avoid contributing to
            // Cloudflare's worker-failure-rate guard rail that
            // surfaces as 503 to the user when the Worker has been
            // 5xx'ing too often.
            //
            // STRUCTURAL errors (bad URL, missing param, auth fail,
            // host not allowed) still return 4xx above — those are
            // real client mistakes we want visible in logs.
            const msg = (e && e.message) || String(e);
            const indexless =
                msg.includes('no SeekHead') ||
                msg.includes('no Tracks pointer') ||
                msg.includes('no index');
            const networkDrop =
                msg.includes('Network connection lost') ||
                msg.includes('fetch failed') ||
                msg.includes('upstream HTTP') ||
                msg.includes('Memory limit') ||
                msg.includes('Range ignored');
            return new Response(JSON.stringify({
                tracks: [],
                extracted: false,
                reason: indexless ? 'indexless' : (networkDrop ? 'network' : 'parse'),
                error: msg,
            }), { status: 200, headers: corsHeaders() });
        }
}
