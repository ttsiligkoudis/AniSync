// Instant tap feedback + navigation cue for poster cards.
//
// Two things make a card tap feel unresponsive on mobile:
//   1. Browsers withhold the :active state on links for a beat while they
//      decide whether the touch is a tap or the start of a scroll, so the
//      built-in press feedback lands late — long enough for the user to
//      wonder "did that register?".
//   2. A card is a plain <a href>, so opening it is a full page load with
//      no "working" cue during the 1-2s the next page takes to paint.
//
// Fix: drive an .is-pressing class from pointerdown (which fires the instant
// the finger lands, before the tap/scroll disambiguation) so the card
// visibly depresses immediately, and clear it on release / scroll / drift.
// On the click that actually navigates this tab, kick the shared loader
// overlay so the page-load wait isn't a dead zone.
(function () {
    'use strict';

    var PRESS_CLASS = 'is-pressing';
    // Press targets. Inert cards (anonymous Discover) don't navigate, so
    // they're excluded — pressing something that does nothing is worse than
    // no feedback.
    var CARD_SEL = '.library-card:not(.library-card-inert)';
    // Pointer drift past this (px) means the gesture turned into a scroll;
    // drop the press so a swipe down a grid doesn't leave cards lit up.
    var MOVE_CANCEL_PX = 10;
    // Same grace window loader.js uses, so a fast (cached) navigation
    // doesn't flash the spinner before the next page paints.
    var NAV_GRACE_MS = 150;

    var pressed = null;
    var startX = 0;
    var startY = 0;
    var navTimer = null;

    function clearPress() {
        if (pressed) {
            pressed.classList.remove(PRESS_CLASS);
            pressed = null;
        }
    }

    function pressableFrom(target) {
        if (!target || !target.closest) return null;
        // The +1 quick-action has its own affordance and doesn't navigate;
        // a tap on it shouldn't depress the whole card.
        if (target.closest('.library-card-plus')) return null;
        return target.closest(CARD_SEL);
    }

    function onPointerDown(e) {
        // Primary button / touch / pen only — ignore right/middle click.
        if (e.button !== undefined && e.button !== 0) return;
        var card = pressableFrom(e.target);
        if (!card) return;
        clearPress();
        pressed = card;
        startX = e.clientX;
        startY = e.clientY;
        card.classList.add(PRESS_CLASS);
    }

    function onPointerMove(e) {
        if (!pressed) return;
        if (Math.abs(e.clientX - startX) > MOVE_CANCEL_PX
            || Math.abs(e.clientY - startY) > MOVE_CANCEL_PX) {
            clearPress();
        }
    }

    document.addEventListener('pointerdown', onPointerDown, { passive: true });
    document.addEventListener('pointermove', onPointerMove, { passive: true });
    document.addEventListener('pointerup', clearPress, { passive: true });
    document.addEventListener('pointercancel', clearPress, { passive: true });
    // Scroll cancels the press immediately (capture so inner scrollers —
    // the horizontal poster shelves — are caught too; scroll doesn't bubble).
    window.addEventListener('scroll', clearPress, { passive: true, capture: true });

    // Navigation cue: when a card click actually navigates this tab, show
    // the loader overlay after a short grace so the page-load gap reads as
    // "working". Uses show()/hide() rather than the request counter so it
    // never tangles with loader.js's in-flight-fetch bookkeeping.
    document.addEventListener('click', function (e) {
        if (e.button !== undefined && e.button !== 0) return;
        // Modified clicks open a new tab / download — current page stays put,
        // so a loader would be misleading.
        if (e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        if (e.defaultPrevented) return;
        var card = pressableFrom(e.target);
        if (!card || !card.getAttribute('href')) return;
        var tgt = card.getAttribute('target');
        if (tgt && tgt !== '_self') return; // target="_blank" etc. — no nav here
        if (!window.AniSyncLoader || !window.AniSyncLoader.show) return;
        if (navTimer) clearTimeout(navTimer);
        navTimer = setTimeout(function () {
            navTimer = null;
            window.AniSyncLoader.show();
        }, NAV_GRACE_MS);
    });

    // Returning via the back button can restore this page from the bfcache
    // with the overlay still shown / a press still stuck — reset both so the
    // restored page isn't frozen behind a spinner.
    window.addEventListener('pageshow', function () {
        clearPress();
        if (navTimer) { clearTimeout(navTimer); navTimer = null; }
        if (window.AniSyncLoader && window.AniSyncLoader.hide) window.AniSyncLoader.hide();
    });
})();
