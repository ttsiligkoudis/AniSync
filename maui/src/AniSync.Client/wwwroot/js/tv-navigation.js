// TV / D-pad remote navigation.
//
// Android TV, Google TV and Fire TV drive the UI with a directional pad
// (up / down / left / right + OK + Back) — no touch, no mouse pointer. A web
// UI tuned for tap/click is unusable there for two reasons:
//   1. Nothing reads as "selected" from across the room — the default 2px
//      focus ring is invisible from a couch.
//   2. Arrow keys fall back to the browser's *linear DOM order*, which jumps
//      around a 2-D poster grid nonsensically (end of one row → start of the
//      next visually-unrelated element).
//
// This script — active only in "TV mode" — fixes both:
//   * Spatial navigation: arrow keys move focus to the nearest focusable
//     element in that *physical* direction, so a remote walks the grid the
//     way the eye expects.
//   * A starting focus: auto-focuses the first sensible control on load so
//     the very first D-pad press has somewhere to land.
//   * OK / Enter activation for click-only widgets (plain native links /
//     buttons / inputs already activate on Enter, so we only synthesise a
//     click for the rest).
//
// Pairs with the `.tv-mode` focus styling in site.css (a big, high-contrast
// ring + lift you can read from across the room). Back is left to the
// platform — Android maps the remote's Back button to history navigation in
// the TWA / WebView, and the app's own Escape handlers already close overlays.
//
// Activation: auto-enables when detectTv() passes (UA / capability heuristic),
// and ALSO exposes window.anisyncTv.enable() so the native MAUI head can force
// it on when DeviceIdiom == TV (IAppEnvironment.IsTv) — that way the C# TV
// shell and this JS focus layer are guaranteed to agree on a real TV even if
// the heuristic would have missed it. enable() is idempotent.
(function () {
    'use strict';

    // --- TV-mode detection -------------------------------------------------
    // Order: explicit opt-in (so it's testable on a desktop with arrow keys),
    // then a UA sniff for the common couch platforms, then a capability
    // heuristic. The heuristic requires zero touch points so it can't trip on
    // a large touch tablet — real TVs report no touch and no hover.
    function detectTv() {
        try {
            var p = new URLSearchParams(location.search);
            if (p.get('tv') === '1') localStorage.setItem('anisync-tv', '1');
            if (p.get('tv') === '0') localStorage.removeItem('anisync-tv');
            if (localStorage.getItem('anisync-tv') === '1') return true;
        } catch (_) { /* storage blocked — fall through to UA / capability */ }

        var ua = navigator.userAgent || '';
        if (/Android TV|GoogleTV|SMART-TV|SmartTV|AFT[A-Z]|BRAVIA|Web0S|WebOS|Tizen|HbbTV|NetCast|DTV/i.test(ua)) {
            return true;
        }
        try {
            if (window.matchMedia('(hover: none)').matches
                && (navigator.maxTouchPoints || 0) === 0
                && window.innerWidth >= 1280) {
                return true;
            }
        } catch (_) { /* no matchMedia — assume not a TV */ }
        return false;
    }

    // --- Focusable discovery ----------------------------------------------
    var FOCUSABLE = [
        'a[href]',
        'button:not([disabled])',
        'input:not([disabled]):not([type="hidden"])',
        'select:not([disabled])',
        'textarea:not([disabled])',
        '[tabindex]:not([tabindex="-1"])',
        '[role="button"]:not([aria-disabled="true"])',
        '.library-card:not(.library-card-inert)'
    ].join(',');

    function isVisible(el) {
        if (el.hidden) return false;
        if (el.closest('[hidden], [aria-hidden="true"]')) return false;
        var r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0) return false;
        // Fully off-screen (collapsed / scrolled far away counts as on-screen
        // — we scrollIntoView after focusing — but a 0-area element doesn't).
        var cs = getComputedStyle(el);
        if (cs.visibility === 'hidden' || cs.display === 'none' || cs.pointerEvents === 'none') {
            return false;
        }
        return true;
    }

    // When a modal/dialog is open, focus must stay INSIDE it — otherwise the D-pad
    // wanders onto the elements behind the scrim (which are still in the DOM and
    // "visible"), so a remote can never reliably land on the modal's own Continue /
    // Close buttons, and OK activates the wrong thing. We scope all focus discovery
    // to the topmost open dialog while one is showing.
    var DIALOG_SEL = '[role="dialog"], .mt-modal, .watch-player-modal';

    function openModal() {
        var nodes = document.querySelectorAll(DIALOG_SEL);
        // Last match wins — later in the DOM ≈ stacked on top.
        for (var i = nodes.length - 1; i >= 0; i--) {
            var el = nodes[i];
            if (!el.hidden && !el.closest('[hidden], [aria-hidden="true"]') && isVisible(el)) {
                return el;
            }
        }
        return null;
    }

    function candidates() {
        var root = openModal() || document;
        var nodes = root.querySelectorAll(FOCUSABLE);
        var out = [];
        for (var i = 0; i < nodes.length; i++) {
            if (isVisible(nodes[i])) out.push(nodes[i]);
        }
        return out;
    }

    function firstFocusable() {
        var c = candidates();
        return c.length ? c[0] : null;
    }

    // --- Spatial pick ------------------------------------------------------
    // Classic centre-delta scoring within a 45° cone: a candidate only counts
    // if it lies more in the travel direction than across it, then we minimise
    // (distance-along-axis + 2× cross-axis drift) so we prefer the element
    // most directly in line, nearest first.
    function centre(el) {
        var r = el.getBoundingClientRect();
        return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    }

    function pickInDirection(current, dir) {
        var cc = centre(current);
        var best = null;
        var bestScore = Infinity;
        candidates().forEach(function (el) {
            if (el === current) return;
            var c = centre(el);
            var dx = c.x - cc.x;
            var dy = c.y - cc.y;
            var primary, cross;
            if (dir === 'left') {
                if (dx >= -1 || Math.abs(dy) > Math.abs(dx)) return;
                primary = -dx; cross = Math.abs(dy);
            } else if (dir === 'right') {
                if (dx <= 1 || Math.abs(dy) > Math.abs(dx)) return;
                primary = dx; cross = Math.abs(dy);
            } else if (dir === 'up') {
                if (dy >= -1 || Math.abs(dx) > Math.abs(dy)) return;
                primary = -dy; cross = Math.abs(dx);
            } else { // down
                if (dy <= 1 || Math.abs(dx) > Math.abs(dy)) return;
                primary = dy; cross = Math.abs(dx);
            }
            var score = primary + cross * 2;
            if (score < bestScore) { bestScore = score; best = el; }
        });
        return best;
    }

    function focusEl(el) {
        if (!el) return;
        el.focus({ preventScroll: true });
        // Centre the newly-focused element so the grid scrolls under the
        // selection rather than the selection running off-screen. Use an INSTANT
        // jump (not behavior:'smooth') — animated scrolling is visibly janky on
        // low-powered TV boxes and lags behind fast D-pad repeats; an instant
        // recentre feels snappier and costs nothing to animate.
        try {
            el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'auto' });
        } catch (_) {
            el.scrollIntoView();
        }
    }

    // --- Key handling ------------------------------------------------------
    var DIRS = {
        ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down',
        Left: 'left', Right: 'right', Up: 'up', Down: 'down'
    };

    function onKeydown(e) {
        if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey) return;
        var ae = document.activeElement;
        var dir = DIRS[e.key];

        if (dir) {
            // Leave arrow keys to text fields for caret movement; only allow
            // up/down to "escape" a single-line input, and let SELECT keep all
            // four for its native option list.
            if (ae) {
                if (ae.tagName === 'SELECT') return;
                if ((ae.tagName === 'INPUT' || ae.tagName === 'TEXTAREA')
                    && (dir === 'left' || dir === 'right')) {
                    return;
                }
            }
            // If a modal is open and focus is still on something behind it, pull it
            // into the modal first so navigation begins inside the dialog.
            var modal = openModal();
            var inScope = !modal || (ae && modal.contains(ae));
            var start = inScope
                ? ((ae && ae !== document.body) ? ae : firstFocusable())
                : firstFocusable();
            if (!start) return;
            if (!inScope) { e.preventDefault(); focusEl(start); return; }
            var next = pickInDirection(start, dir);

            // Right from the left rail always leaves it (so it collapses): go to the content
            // element to the right if there is one, otherwise just move focus into the content
            // (or blur) — never get stuck expanded when the page has nothing to the right.
            if (dir === 'right' && start.closest && start.closest('.tv-rail')) {
                e.preventDefault();
                if (next && !(next.closest && next.closest('.tv-rail'))) focusEl(next);
                else focusContent();
                return;
            }

            if (next) {
                e.preventDefault();
                focusEl(next);
            } else if (!ae || ae === document.body) {
                // Nothing focused yet — land somewhere on the first press.
                e.preventDefault();
                focusEl(start);
            }
            // No candidate in that direction with focus already set: let the
            // default happen (page scroll), which may reveal more to focus.
            return;
        }

        if (e.key === 'Enter' || e.key === 'OK') {
            // With a modal open but focus still behind it, don't activate the
            // background element — move into the dialog instead.
            var modal = openModal();
            if (modal && (!ae || !modal.contains(ae))) {
                e.preventDefault();
                focusEl(firstFocusable());
                return;
            }
            // Native links / buttons / form controls activate on Enter on
            // their own; only synthesise a click for click-only widgets.
            if (ae && !/^(A|BUTTON|INPUT|TEXTAREA|SELECT)$/.test(ae.tagName)) {
                e.preventDefault();
                ae.click();
            }
        }
    }

    // --- Initial focus -----------------------------------------------------
    function autoFocus() {
        if (document.activeElement && document.activeElement !== document.body) return;
        focusEl(document.querySelector('[autofocus]') || firstFocusable());
    }

    // Move focus out of the left rail into the main content after a navigation, so the rail
    // collapses (it expands on :focus-within) and the D-pad lands in the page. Called by the
    // TV shell on LocationChanged. Retries a couple of frames because the destination content
    // renders just after the route change.
    function focusContent(attempt) {
        attempt = attempt || 0;
        var scope = document.querySelector('.tv-content');
        var target = null;
        if (scope) {
            var nodes = scope.querySelectorAll(FOCUSABLE);
            for (var i = 0; i < nodes.length; i++) { if (isVisible(nodes[i])) { target = nodes[i]; break; } }
        }
        if (target) {
            focusEl(target);
        } else {
            // Nothing focusable in the content yet (e.g. the QR sign-in screen) — at least drop
            // focus from the rail so it collapses; retry briefly in case content is still rendering.
            try { if (document.activeElement && document.activeElement.blur) document.activeElement.blur(); } catch (_) { }
            if (attempt < 3) setTimeout(function () { focusContent(attempt + 1); }, 120);
        }
    }

    // --- Enable (idempotent) ----------------------------------------------
    var _enabled = false;
    function enable() {
        if (_enabled) return;
        _enabled = true;
        try { localStorage.setItem('anisync-tv', '1'); } catch (_) { /* storage blocked */ }
        document.documentElement.classList.add('tv-mode');
        document.addEventListener('keydown', onKeydown);
        if (document.readyState !== 'loading') autoFocus();
        else document.addEventListener('DOMContentLoaded', autoFocus);
    }

    // The native head can force this on (DeviceIdiom == TV) so the C# shell and
    // the JS focus layer never disagree; autoFocus + focusContent let the shell re-land
    // focus after a SPA navigation (and collapse the rail by moving focus into the content).
    window.anisyncTv = { enable: enable, autoFocus: autoFocus, focusContent: focusContent };

    if (detectTv()) enable();
})();
