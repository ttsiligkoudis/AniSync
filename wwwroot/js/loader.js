// Global request loader. Sits as a fixed full-viewport overlay (rendered
// in _Layout.cshtml) and lights up whenever HTTP work is in flight, so
// the user always has a "something is happening" cue regardless of which
// code path made the request.
//
// Hooks:
//   - window.fetch is monkey-patched to bracket each call with
//     beginRequest / endRequest. Catches the manage-entry modal, PWA
//     install plumbing, and anything else using the modern fetch API.
//   - jQuery's global ajaxSend / ajaxComplete events bracket each
//     $.ajax call. Catches the standalone ManageEntry view and any
//     legacy jQuery code paths.
//
// A single shared counter tracks in-flight work; the overlay only hides
// when the counter drains to zero, so two concurrent fetches don't have
// the second hide racing the first.
//
// A 150ms grace delay before show() avoids the spinner flickering for
// requests that resolve in under a frame (cached responses, fast 304s).
//
// Public API on window.AniSyncLoader for explicit use:
//   .show() / .hide()      — force the overlay state
//   .beginRequest() / .endRequest() — manual counter ticks for non-
//      fetch / non-jQuery async work (image preload, WebSocket, etc.)
(function () {
    'use strict';

    var counter = 0;
    var showTimer = null;
    var GRACE_MS = 150;

    function getOverlay() {
        return document.getElementById('global-loader-overlay');
    }

    function show() {
        var el = getOverlay();
        if (!el) return;
        el.classList.add('show');
        el.setAttribute('aria-hidden', 'false');
    }

    function hide() {
        var el = getOverlay();
        if (!el) return;
        el.classList.remove('show');
        el.setAttribute('aria-hidden', 'true');
    }

    function beginRequest() {
        counter++;
        if (counter === 1) {
            if (showTimer) clearTimeout(showTimer);
            showTimer = setTimeout(function () {
                showTimer = null;
                show();
            }, GRACE_MS);
        }
    }

    function endRequest() {
        counter = Math.max(0, counter - 1);
        if (counter === 0) {
            if (showTimer) {
                clearTimeout(showTimer);
                showTimer = null;
            }
            hide();
        }
    }

    // Stamp X-Requested-With on every same-origin non-GET request so the
    // server-side CsrfOrAjaxFilter accepts it without an antiforgery token
    // round-trip. Browsers refuse to attach custom headers cross-origin
    // without a CORS preflight, and the in-app endpoints don't allow that
    // preflight, so an attacker can't forge this header from outside —
    // it's effectively a same-origin marker and equivalent to an antiforgery
    // token for CSRF defence. Cross-origin opt-outs (manifest URLs typed
    // into Stremio web, etc.) pass init.crossOrigin = true.
    function stampCsrfHeader(init) {
        var method = (init && init.method ? String(init.method) : 'GET').toUpperCase();
        if (method === 'GET' || method === 'HEAD' || method === 'OPTIONS') return init;
        if (init && init.crossOrigin === true) {
            var cleaned = Object.assign({}, init);
            delete cleaned.crossOrigin;
            return cleaned;
        }
        var headers = new Headers(init && init.headers ? init.headers : undefined);
        if (!headers.has('X-Requested-With')) headers.set('X-Requested-With', 'XMLHttpRequest');
        return Object.assign({}, init || {}, { headers: headers });
    }

    // fetch wrap. Capture the original via .bind so calls keep the
    // expected `this` (window). The .finally hook fires for both
    // fulfilled and rejected promises so we never leak the counter.
    //
    // Opt-out: pass init.skipLoader = true to bypass the counter for
    // high-frequency / interactive requests where flashing the spinner
    // would be noisy (typeahead search, autosave, polling). The flag is
    // stripped from init before reaching origFetch so the browser doesn't
    // emit it as part of any request shape.
    if (typeof window.fetch === 'function') {
        var origFetch = window.fetch.bind(window);
        window.fetch = function (input, init) {
            var skipLoader = init && init.skipLoader === true;
            if (skipLoader) {
                // Shallow-clone init so we can remove the non-standard key
                // without mutating the caller's options object.
                var cleaned = Object.assign({}, init);
                delete cleaned.skipLoader;
                return origFetch(input, stampCsrfHeader(cleaned));
            }
            beginRequest();
            var p = origFetch(input, stampCsrfHeader(init));
            // .finally is supported everywhere we care about (Chromium 63+,
            // Safari 11.1+, Firefox 58+) — well below our PWA install base.
            p.finally(endRequest);
            return p;
        };
    }

    // jQuery ajax hooks. jQuery's own ajaxStart/ajaxStop are coarse
    // (only fire on the first/last in a batch), so we use ajaxSend /
    // ajaxComplete instead — those fire per-request and feed the same
    // counter as the fetch wrap. The X-Requested-With header is set
    // automatically by jQuery on every $.ajax call, so the CSRF filter
    // accepts these without further work.
    if (window.jQuery) {
        window.jQuery(document).on('ajaxSend', beginRequest);
        window.jQuery(document).on('ajaxComplete', endRequest);
    }

    window.AniSyncLoader = {
        show: show,
        hide: hide,
        beginRequest: beginRequest,
        endRequest: endRequest,
    };
})();
