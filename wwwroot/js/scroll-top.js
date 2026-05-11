// Back-to-top button. Hidden until the page has scrolled far enough that
// scrolling manually back up is a noticeable distance (400px), then
// fades in. Clicking smooth-scrolls to the top.
//
// The button lives in _Layout.cshtml so every page picks it up
// automatically; this script just toggles its [hidden] attribute and
// wires the click handler.
(function () {
    'use strict';

    var btn = document.getElementById('scroll-top-btn');
    if (!btn) return;

    // Threshold past which the button is meaningful. Below this, the user
    // can scroll up faster than the button-tap → scroll-animation roundtrip,
    // so showing it would just be visual clutter.
    var SHOW_AFTER_PX = 400;

    // Throttle the scroll handler via requestAnimationFrame — `scroll`
    // events fire at every paint frame on some browsers so a naive handler
    // burns CPU on long pages.
    var ticking = false;
    function onScroll() {
        if (ticking) return;
        ticking = true;
        window.requestAnimationFrame(function () {
            var y = window.pageYOffset || document.documentElement.scrollTop || 0;
            btn.hidden = y < SHOW_AFTER_PX;
            ticking = false;
        });
    }

    btn.addEventListener('click', function () {
        // prefers-reduced-motion users skip the animation — instant jump
        // is honest about their preference.
        var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        window.scrollTo({ top: 0, behavior: reduce ? 'auto' : 'smooth' });
    });

    window.addEventListener('scroll', onScroll, { passive: true });
    // Run once at load so the button picks up its visible state on
    // pages opened mid-scroll (back-nav restoring scroll position, etc.).
    onScroll();
})();
