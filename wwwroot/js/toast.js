// Tiny toast component shared across the web-app pages. Two responsibilities:
//
//   1. On every page load, check sessionStorage for a "anisync-toast" flag set
//      by a previous action (typically the Manage Entry modal's save flow which
//      reloads the page) and show the message briefly. The sessionStorage hop
//      lets a toast survive a full page reload — a real "saved!" feedback
//      moment without requiring the saving JS to skip the reload.
//   2. Expose window.AniSyncToast.show(text) for in-page actions that don't
//      reload (future optimistic-update flows).
//
// Auto-dismiss after 2.5s. No close button — toasts here are
// confirmations of completed actions, not prompts for further interaction.
(function () {
    'use strict';

    var DEFAULT_DURATION_MS = 3000;

    function showToast(text, durationMs) {
        var container = document.getElementById('toast-container');
        if (!container || !text) return;
        var el = document.createElement('div');
        el.className = 'toast';
        el.setAttribute('role', 'status');
        el.textContent = text;
        container.appendChild(el);
        // CSS animation handles the fade-in/fade-out; the JS just removes the
        // element after the animation finishes so DOM stays tidy. Caller can
        // pass a longer duration for messages that carry multi-step
        // instructions and need actual reading time.
        var lifespan = typeof durationMs === 'number' && durationMs > 0
            ? durationMs
            : DEFAULT_DURATION_MS;
        setTimeout(function () { el.remove(); }, lifespan);
    }

    // Pop a queued toast from the previous page render, if any.
    try {
        var queued = sessionStorage.getItem('anisync-toast');
        if (queued) {
            sessionStorage.removeItem('anisync-toast');
            showToast(queued);
        }
    } catch (e) {
        // sessionStorage can throw in strict private-browsing contexts; the
        // toast is best-effort UX so silently swallow.
    }

    window.AniSyncToast = { show: showToast };
})();
