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

    // showToast(text)                       — confirmation, auto-dismiss
    // showToast(text, 6000)                 — legacy: custom duration in ms
    // showToast(text, { action, onAction,   — actionable / sticky toast:
    //                   persist, duration })    a tappable "action" affordance,
    //                                           optionally never auto-dismissing
    //                                           (persist) until tapped.
    function showToast(text, opts) {
        var container = document.getElementById('toast-container');
        if (!container || !text) return null;

        // Normalise the overloaded second argument.
        var o = (typeof opts === 'object' && opts !== null) ? opts
              : { duration: (typeof opts === 'number' ? opts : undefined) };

        var el = document.createElement('div');
        el.className = 'toast';
        el.setAttribute('role', 'status');

        var msg = document.createElement('span');
        msg.className = 'toast-msg';
        msg.textContent = text;
        el.appendChild(msg);

        function dismiss() { el.remove(); }

        if (o.action && typeof o.onAction === 'function') {
            el.classList.add('toast-actionable');
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'toast-action';
            btn.textContent = o.action;
            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                o.onAction();
                dismiss();
            });
            el.appendChild(btn);
        }

        container.appendChild(el);

        // Persisted toasts stay until tapped/actioned; everything else auto-
        // dismisses. CSS handles the fade; JS just tidies the DOM afterwards.
        if (!o.persist) {
            var lifespan = typeof o.duration === 'number' && o.duration > 0
                ? o.duration
                : DEFAULT_DURATION_MS;
            setTimeout(dismiss, lifespan);
        }
        return { dismiss: dismiss };
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

    // Haptics — a subtle tactile tick on key actions (mark-watched, +1
    // episode, pull-to-refresh trigger). Android honours the Vibration API;
    // iOS browsers ignore it, so feature-detect and no-op everywhere else.
    window.AniSyncHaptics = {
        tick: function (ms) {
            try { if (navigator.vibrate) navigator.vibrate(ms || 10); }
            catch (_) { /* blocked / unsupported — silent no-op */ }
        }
    };
})();
