// Lazy loader for the Web-head video stack. ArtPlayer (third-party) + watch-player.js
// (the ~66 KB anisyncWatch driver) are only needed on /watch, so they're no longer in
// App.razor on every page. Html5MediaPlayer calls ensure() once, on the first source
// play, before invoking anisyncWatch.* — this injects both scripts and resolves as soon
// as window.anisyncWatch is defined. ArtPlayer is injected too but not awaited:
// watch-player.js's play() already polls window.Artplayer for ~6s, so the engine just
// needs to be on its way. Idempotent (one-shot promise; no-op once anisyncWatch exists).
let _ready = null;

export function ensure(artplayerUrl, watchPlayerUrl) {
    if (window.anisyncWatch) return Promise.resolve();
    if (_ready) return _ready;
    // Kick off ArtPlayer; play() polls for window.Artplayer so we don't block on it.
    injectScript(artplayerUrl).catch(function () { /* play() surfaces a timeout fallback */ });
    _ready = injectScript(watchPlayerUrl).then(function () {
        return waitFor(function () { return !!window.anisyncWatch; });
    });
    return _ready;
}

function injectScript(src) {
    return new Promise(function (resolve, reject) {
        var existing = document.querySelector('script[data-lazy-src="' + src + '"]');
        if (existing) {
            if (existing.getAttribute('data-loaded') === '1') { resolve(); return; }
            existing.addEventListener('load', function () { resolve(); });
            existing.addEventListener('error', function () { reject(new Error('load failed: ' + src)); });
            return;
        }
        var s = document.createElement('script');
        s.src = src;
        s.async = false; // preserve insertion order (harmless; play() polls anyway)
        s.setAttribute('data-lazy-src', src);
        s.addEventListener('load', function () { s.setAttribute('data-loaded', '1'); resolve(); });
        s.addEventListener('error', function () { reject(new Error('load failed: ' + src)); });
        document.head.appendChild(s);
    });
}

function waitFor(cond, timeoutMs) {
    timeoutMs = timeoutMs || 8000;
    return new Promise(function (resolve) {
        var start = Date.now();
        (function poll() {
            if (cond() || Date.now() - start > timeoutMs) { resolve(); return; }
            setTimeout(poll, 30);
        })();
    });
}
