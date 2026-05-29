// One-shot localStorage sweeper. Runs on every page load (deferred
// via requestIdleCallback so it never blocks the first paint) and
// removes stale entries from the two TTL-bounded caches AniSync
// writes to the browser:
//
//   anisync.streams.{uid}.{episodeKey}   — 10-minute reuse window
//                                          for the per-episode
//                                          stream-addon fan-out
//                                          result (see Watch.cshtml)
//
//   anisync.resume.{uid}.{animeId}|s=N|e=N — 90-day per-episode
//                                            playback position
//
// The pages that read these caches already do lazy expiry on read,
// so a sweeper is belt-and-braces only. The point is to wipe entries
// the user never reopens — a heavy power-browser would otherwise
// accumulate a few hundred stale rows before any one of them
// expires through the on-read path. Cheap (one localStorage.length
// scan, one JSON parse per matching key); the worst observed cost
// is sub-20ms on a thousand-entry localStorage, well below the idle
// budget.
//
// Other keys (anisync.continueWatching.v1, third-party libraries,
// future caches without a {ts: …} shape) are left untouched.
(function () {
    'use strict';

    var STREAM_PREFIX = 'anisync.streams.';
    var STREAM_TTL_MS = 10 * 60 * 1000;

    var RESUME_PREFIX = 'anisync.resume.';
    var RESUME_TTL_MS = 90 * 24 * 60 * 60 * 1000;

    // anisync.lastSource.{uid}.{episodeKey} — which source the user last
    // clicked to play, for the "Last played" marker on the watch page.
    // Tiny ({url, ts}); kept on the same 90-day horizon as resume so it
    // survives until a show is realistically abandoned.
    var LAST_SOURCE_PREFIX = 'anisync.lastSource.';
    var LAST_SOURCE_TTL_MS = 90 * 24 * 60 * 60 * 1000;

    function sweep() {
        var now;
        try { now = Date.now(); } catch (_) { return; }

        var doomed = [];
        var total;
        try { total = localStorage.length; }
        catch (_) { return; /* private mode / disabled storage */ }

        for (var i = 0; i < total; i++) {
            var key;
            try { key = localStorage.key(i); }
            catch (_) { continue; }
            if (!key) continue;

            var ttl = null;
            if (key.indexOf(STREAM_PREFIX) === 0) ttl = STREAM_TTL_MS;
            else if (key.indexOf(RESUME_PREFIX) === 0) ttl = RESUME_TTL_MS;
            else if (key.indexOf(LAST_SOURCE_PREFIX) === 0) ttl = LAST_SOURCE_TTL_MS;
            if (ttl === null) continue;

            var raw;
            try { raw = localStorage.getItem(key); }
            catch (_) { continue; }
            if (!raw) continue;

            // Parse failures and missing ts are treated as "stale" —
            // a row we can't read is dead weight either way and the
            // page that owns the key will rewrite on its next save.
            var ts = -1;
            try {
                var entry = JSON.parse(raw);
                if (entry && typeof entry.ts === 'number') ts = entry.ts;
            } catch (_) { /* fall through with ts = -1 */ }

            if (ts < 0 || now - ts >= ttl) doomed.push(key);
        }

        for (var j = 0; j < doomed.length; j++) {
            try { localStorage.removeItem(doomed[j]); } catch (_) {}
        }
    }

    // requestIdleCallback so the sweep runs in browser-idle time
    // (after first paint, after pending interaction). Fallback to a
    // 0-delay setTimeout on Safari, which still hasn't shipped the
    // API — the worst case is the sweep runs one tick later than
    // ideal, which is fine.
    var schedule = (typeof window !== 'undefined' && window.requestIdleCallback)
        ? function (fn) { window.requestIdleCallback(fn, { timeout: 2000 }); }
        : function (fn) { setTimeout(fn, 0); };

    schedule(sweep);
})();
