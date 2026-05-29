// hover-prefetch.js — cross-browser instant-navigation fallback.
//
// Chromium gets the real thing from the <script type="speculationrules"> block
// in _Layout (prerender/prefetch on hover-intent). This file is the fallback
// for browsers WITHOUT Speculation Rules support (Safari, Firefox): on
// pointer-intent over an internal navigation link it warms a
// <link rel="prefetch"> so the document is already in the HTTP cache by the
// time the user clicks. It mirrors the ruleset's exclusions so it never
// pre-fetches a state-mutating GET (logout), an API route, or an external link.
(function () {
    'use strict';

    // Self-disable where Speculation Rules are supported — that path is
    // strictly better (it can prerender, not just prefetch) and double-warming
    // would waste bandwidth. HTMLScriptElement.supports is the canonical probe.
    try {
        if (window.HTMLScriptElement &&
            typeof HTMLScriptElement.supports === 'function' &&
            HTMLScriptElement.supports('speculationrules')) {
            return;
        }
    } catch (_) { /* probe unavailable — proceed as a non-supporting browser */ }

    // Respect the user's data-saver preference — never speculatively prefetch
    // on a metered/save-data connection.
    try {
        var conn = navigator.connection;
        if (conn && conn.saveData) return;
    } catch (_) { /* no NetworkInformation — proceed */ }

    var warmed = new Set();     // hrefs already prefetched (dedupe)
    var MAX = 15;               // bound speculative work per page

    function shouldSkip(a) {
        if (!a || !a.getAttribute) return true;
        var raw = a.getAttribute('href');
        if (!raw || raw[0] === '#') return true;                  // no href / in-page anchor
        if (a.hasAttribute('download')) return true;
        if (a.target && a.target !== '' && a.target !== '_self') return true; // _blank etc.
        if (a.hasAttribute('data-disconnect')) return true;       // logout/disconnect
        if (a.hasAttribute('data-no-prerender')) return true;     // explicit opt-out
        var rel = (a.getAttribute('rel') || '').toLowerCase();
        if (rel.indexOf('external') !== -1 || rel.indexOf('nofollow') !== -1) return true;

        var url;
        try { url = new URL(a.href, location.href); } catch (_) { return true; }
        if (url.origin !== location.origin) return true;          // cross-origin
        if (url.search) return true;                              // query strings (returnUrl stamps, filter state)
        if (url.pathname === location.pathname) return true;      // current page
        var p = url.pathname.toLowerCase();
        if (p.indexOf('/api') === 0 || p.indexOf('/auth') === 0) return true; // API + auth/logout
        return false;
    }

    function warm(a) {
        if (warmed.size >= MAX) return;
        var url;
        try { url = new URL(a.href, location.href); } catch (_) { return; }
        var key = url.pathname;
        if (warmed.has(key)) return;
        warmed.add(key);
        var link = document.createElement('link');
        link.rel = 'prefetch';
        link.href = url.href;
        link.as = 'document';
        document.head.appendChild(link);
    }

    function onIntent(e) {
        var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (!a || shouldSkip(a)) return;
        warm(a);
    }

    // pointerenter (desktop hover) + pointerdown/touchstart (the moment before a
    // tap commits) cover both input modes. Passive listeners — we never block.
    document.addEventListener('pointerenter', onIntent, { capture: true, passive: true });
    document.addEventListener('pointerdown', onIntent, { capture: true, passive: true });
    document.addEventListener('touchstart', onIntent, { capture: true, passive: true });
})();
