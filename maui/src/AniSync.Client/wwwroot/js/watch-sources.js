// Watch page interop — the two browser-only concerns the source picker needs
// that can't live in Blazor @code:
//
//   1. The localStorage 10-minute TTL stream cache (anisync.streams.{uid}.{key}).
//      Ported verbatim from Views/Meta/Watch.cshtml's readCachedStreamData /
//      writeCachedStreamData. localStorage on purpose: the upstream rate-limit the
//      cache softens is per-id / per-IP, and debrid URLs are signed for the
//      device's IP — a server cache wouldn't help and would pay a round-trip.
//      The negative scenario (empty fan-out) is deliberately never cached so the
//      NEXT visit re-attempts once the rate-limit window opens / the addon warms.
//
//   2. The shared "set up streaming" nudge wiring (the inline <script> at the
//      bottom of Views/Shared/_StreamSetupNudge.cshtml): reveal-after-dismiss-check
//      + the "How it works" expand/collapse + the dismiss persistence. Idempotent
//      and instance-scoped (guarded by a data flag) so a Blazor re-render that
//      re-invokes it never double-binds.

var STREAM_CACHE_TTL_MS = 10 * 60 * 1000;

// Device-direct stream fetch: fetch the addon's /stream/{type}/{id}.json from THIS device
// (browser tab or MAUI WebView) so debrid hosts bind the playback token to the device's own
// IP — not AniSync's backend, whose IP (and IP family) differs from the device's media path.
// Returns the raw JSON text, or null on CORS / network / non-2xx so the caller can fall back
// to the server-side fetch. credentials:'omit' keeps it a simple CORS request (no preflight).
export async function fetchText(url) {
    if (!url) return null;
    try {
        var resp = await fetch(url, { credentials: 'omit', redirect: 'follow' });
        if (!resp.ok) return null;
        return await resp.text();
    } catch (_) {
        return null;   // CORS-blocked / offline — caller falls back to the server fetch
    }
}

// Read the cached fan-out result for an episode. Returns the stored data object
// (the combined { debridStreams, externalLinks, skipTimes, … } shape) or null
// when the key is empty (anonymous — no uid), missing, expired, or unparseable.
export function readCache(key) {
    if (!key) return null;
    var raw;
    try { raw = localStorage.getItem(key); }
    catch (_) { return null; }
    if (!raw) return null;
    try {
        var entry = JSON.parse(raw);
        if (!entry || typeof entry.ts !== 'number') return null;
        if (Date.now() - entry.ts >= STREAM_CACHE_TTL_MS) return null;
        return entry.data || null;
    } catch (_) { return null; }
}

// Persist the final combined fan-out shape. Skips empty debrid lists on purpose
// (see the negative-scenario note above) so callers can fire this unconditionally
// once the fan-out settles.
export function writeCache(key, data) {
    if (!key || !data) return;
    var debrid = (data && data.debridStreams) || [];
    if (debrid.length === 0) return;
    try {
        localStorage.setItem(key, JSON.stringify({ ts: Date.now(), data: data }));
    } catch (_) {
        // Quota / private-mode failures are non-fatal — the page still renders
        // from the live fan-out.
    }
}

export function clearCache(key) {
    if (!key) return;
    try { localStorage.removeItem(key); } catch (_) {}
}

// ── Last-played source marker (anisync.lastSource.{uid}.{key}) ────────────────
// Keyed on the stream URL — the only stable id the client receives. Debrid URLs
// rotate when the cache expires and the fan-out re-runs, so the marker naturally
// lapses after that window rather than pointing at a dead row.
export function readLastSource(key) {
    if (!key) return null;
    try {
        var raw = localStorage.getItem(key);
        if (!raw) return null;
        var obj = JSON.parse(raw);
        return obj && obj.url ? obj.url : null;
    } catch (_) { return null; }
}

export function writeLastSource(key, url) {
    if (!key || !url) return;
    try {
        localStorage.setItem(key, JSON.stringify({ url: url, ts: Date.now() }));
    } catch (_) { /* private mode / quota — best-effort */ }
}

// ── Browser playability heuristics ───────────────────────────────────────────
// Returned to the watch page so it can colour the HEVC badge and run the
// playable-first re-sorts (HEVC-risky / iOS-container) without an eval() call
// (which a strict CSP could block). Mirrors HEVC_RISKY + IS_IOS_SAFARI in
// Views/Meta/Watch.cshtml.
//   hevcRisky — Chromium-based desktop browsers corrupt many real-world HEVC
//     files into macroblock soup with no software fallback. Firefox / Safari /
//     mobile handle HEVC cleanly, so it's only "risky" on desktop Chrome/Edge/…
//   iosSafari — iPad / iPhone Safari can't demux MKV / AVI inline at all.
export function clientFlags() {
    var ua = '';
    try { ua = navigator.userAgent || ''; } catch (_) { }
    var hevcRisky = false;
    try {
        if (!/Mobi|Mobile|Android|iPhone|iPad|iPod/i.test(ua)) {
            hevcRisky = /Chrome|Chromium/.test(ua) && !/Firefox/.test(ua);
        }
    } catch (_) { }
    var iosSafari = false;
    try {
        if (/iPad|iPhone|iPod/.test(ua)) iosSafari = true;
        else if (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1) iosSafari = true;
    } catch (_) { }
    return { hevcRisky: hevcRisky, iosSafari: iosSafari };
}

// ── Shared stream-setup nudge wiring ─────────────────────────────────────────
// Wires every nudge on the page: the "How it works" expand/collapse and, when
// present, the dismiss button (persisted in localStorage so it never nags).
// Idempotent + instance-scoped so it's safe to re-invoke per Blazor render.
export function wireNudges() {
    var DISMISS_KEY = 'anisync.debridNudgeDismissed.v1';
    document.querySelectorAll('[data-stream-nudge]').forEach(function (nudge) {
        if (nudge.dataset.nudgeWired === '1') return;
        nudge.dataset.nudgeWired = '1';

        var dismiss = nudge.querySelector('[data-stream-nudge-dismiss]');
        if (dismiss) {
            var gone = false;
            try { gone = localStorage.getItem(DISMISS_KEY) === '1'; } catch (e) { }
            if (gone) { nudge.remove(); return; }
            // Reveal now that we know it wasn't dismissed (markup ships hidden to
            // avoid a flash before this check).
            nudge.hidden = false;
            dismiss.addEventListener('click', function () {
                try { localStorage.setItem(DISMISS_KEY, '1'); } catch (e) { }
                nudge.remove();
            });
        } else {
            nudge.hidden = false;
        }

        var toggle = nudge.querySelector('[data-stream-nudge-toggle]');
        if (toggle) {
            toggle.addEventListener('click', function () {
                var open = nudge.classList.toggle('is-open');
                toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
            });
        }
    });
}
