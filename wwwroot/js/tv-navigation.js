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

    if (!detectTv()) return;
    document.documentElement.classList.add('tv-mode');

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

    function candidates() {
        var nodes = document.querySelectorAll(FOCUSABLE);
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
        // selection rather than the selection running off-screen.
        try {
            el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
        } catch (_) {
            el.scrollIntoView();
        }
    }

    // --- Key handling ------------------------------------------------------
    var DIRS = {
        ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down',
        Left: 'left', Right: 'right', Up: 'up', Down: 'down'
    };

    document.addEventListener('keydown', function (e) {
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
            var start = (ae && ae !== document.body) ? ae : firstFocusable();
            if (!start) return;
            var next = pickInDirection(start, dir);
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
            // Native links / buttons / form controls activate on Enter on
            // their own; only synthesise a click for click-only widgets.
            if (ae && !/^(A|BUTTON|INPUT|TEXTAREA|SELECT)$/.test(ae.tagName)) {
                e.preventDefault();
                ae.click();
            }
        }
    });

    // --- Initial focus -----------------------------------------------------
    function autoFocus() {
        if (document.activeElement && document.activeElement !== document.body) return;
        focusEl(document.querySelector('[autofocus]') || firstFocusable());
    }
    if (document.readyState !== 'loading') autoFocus();
    else document.addEventListener('DOMContentLoaded', autoFocus);
})();
