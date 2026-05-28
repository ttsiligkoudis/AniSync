// Pull-to-refresh (item 13). A hand-rolled gesture (no dependency) that
// makes the service-worker "cached then revalidate" story feel intentional:
// pull down from the top of the page to reload.
//
// Deliberately scoped:
//   • Only in installed / standalone display-mode — in a browser tab the
//     platform already provides its own pull-to-refresh and we must not
//     fight it.
//   • Not on the Watch page (the video player owns vertical gestures).
//   • Suppressed while a modal or the nav drawer is open.
(function () {
    'use strict';

    function isStandalone() {
        return (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) ||
               window.navigator.standalone === true;
    }
    if (!isStandalone()) return;
    // The Watch page's player + swipe-nav own vertical/horizontal gestures.
    if (document.getElementById('watchPlayer')) return;

    var THRESHOLD = 80;   // px (post-damping) to arm a refresh
    var MAX = 140;        // px cap on how far the indicator travels
    var DAMP = 0.5;       // resistance applied to the raw finger travel

    var startY = 0, dist = 0, pulling = false;

    var indicator = document.createElement('div');
    indicator.className = 'ptr-indicator';
    indicator.setAttribute('aria-hidden', 'true');
    indicator.innerHTML = '<span class="ptr-spinner"></span>';

    function atTop() {
        return (window.scrollY || document.documentElement.scrollTop || 0) <= 0;
    }
    function blocked() {
        return document.body.classList.contains('modal-open') ||
               document.body.classList.contains('drawer-open');
    }
    function pullDistance() {
        return Math.min(dist * DAMP, MAX);
    }

    function place(y, opacity) {
        indicator.style.transform = 'translateX(-50%) translateY(' + y + 'px)';
        indicator.style.opacity = String(opacity);
    }

    function reset() {
        pulling = false;
        indicator.classList.remove('ptr-ready');
        indicator.style.transition = 'transform 0.2s ease, opacity 0.2s ease';
        place(0, 0);
        setTimeout(function () { indicator.style.transition = ''; }, 220);
    }

    function ensureMounted() {
        if (!indicator.isConnected) document.body.appendChild(indicator);
    }

    window.addEventListener('touchstart', function (e) {
        if (e.touches.length !== 1 || !atTop() || blocked()) { pulling = false; return; }
        ensureMounted();
        startY = e.touches[0].clientY;
        dist = 0;
        pulling = true;
    }, { passive: true });

    window.addEventListener('touchmove', function (e) {
        if (!pulling) return;
        dist = e.touches[0].clientY - startY;
        if (dist <= 0 || !atTop()) { reset(); return; }
        var pull = pullDistance();
        place(pull, Math.min(pull / THRESHOLD, 1));
        indicator.classList.toggle('ptr-ready', pull >= THRESHOLD);
    }, { passive: true });

    function end() {
        if (!pulling) return;
        var pull = pullDistance();
        if (pull >= THRESHOLD) {
            pulling = false;
            indicator.classList.add('ptr-refreshing');
            place(THRESHOLD, 1);
            if (window.AniSyncHaptics) window.AniSyncHaptics.tick();
            window.location.reload();
        } else {
            reset();
        }
    }
    window.addEventListener('touchend', end, { passive: true });
    window.addEventListener('touchcancel', reset, { passive: true });
})();
