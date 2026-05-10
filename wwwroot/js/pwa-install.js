// PWA install handling: capture the browser's install prompt event,
// expose a custom "Install app" pill in the site header instead of
// waiting for the browser's own (less discoverable) chip, and
// surface a one-time iOS instructions toast since iOS Safari doesn't
// fire beforeinstallprompt at all.
//
// The pill stays hidden by default — only revealed once the browser
// signals install eligibility (engagement criteria + manifest checks
// passed). Clicks call .prompt() on the saved event; success removes
// the pill via the appinstalled event.
(function () {
    'use strict';

    // Register the service worker first — install eligibility on
    // Chromium requires an active SW. Fire-and-forget; failure here
    // doesn't break anything user-facing.
    if ('serviceWorker' in navigator) {
        window.addEventListener('load', function () {
            navigator.serviceWorker.register('/sw.js').catch(function () {
                // ignore — falls back to a non-installable site
            });
        });
    }

    var installBtn = document.querySelector('[data-pwa-install]');
    var deferredPrompt = null;

    // Helper: detect "already installed". navigator.standalone is
    // iOS-only (Safari sets it true when launched from Home Screen);
    // matchMedia('display-mode: standalone') covers Android/desktop.
    function isStandalone() {
        return (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) ||
               window.navigator.standalone === true;
    }

    // Chromium / Edge / Android Chrome path. Browser fires this when
    // the page meets the install criteria; preventDefault() suppresses
    // the browser's own banner so we can show our pill instead.
    window.addEventListener('beforeinstallprompt', function (e) {
        e.preventDefault();
        deferredPrompt = e;
        if (installBtn && !isStandalone()) {
            installBtn.hidden = false;
        }
    });

    if (installBtn) {
        installBtn.addEventListener('click', function () {
            if (!deferredPrompt) return;
            deferredPrompt.prompt();
            deferredPrompt.userChoice.then(function () {
                // Hide regardless — the prompt can only fire once per
                // event instance. The appinstalled handler hides on
                // accept; for a dismissed prompt the pill stays hidden
                // until the next pageload (deferredPrompt is exhausted).
                installBtn.hidden = true;
                deferredPrompt = null;
            });
        });
    }

    window.addEventListener('appinstalled', function () {
        if (installBtn) installBtn.hidden = true;
        deferredPrompt = null;
    });

    // iOS Safari instructions card. The browser doesn't fire
    // beforeinstallprompt, so the only way to nudge the user is a
    // manual "tap Share → Add to Home Screen" hint. Show once,
    // remember dismissal in localStorage so it doesn't pester.
    function maybeShowIosHint() {
        if (isStandalone()) return;
        var ua = navigator.userAgent || '';
        var isIos = /iPad|iPhone|iPod/.test(ua) && !window.MSStream;
        // Exclude Chrome / Firefox / Edge on iOS — they share the same
        // WebKit engine but show a different UI; better to skip than
        // give wrong instructions.
        var isSafari = /^((?!chrome|crios|fxios|edgios).)*safari/i.test(ua);
        if (!isIos || !isSafari) return;
        try {
            if (localStorage.getItem('anisync-pwa-ios-dismissed') === '1') return;
        } catch (e) { /* private mode — skip the dismiss memory */ }

        var hint = document.createElement('div');
        hint.className = 'pwa-ios-hint';
        hint.setAttribute('role', 'dialog');
        hint.innerHTML =
            '<div class="pwa-ios-hint-body">' +
                '<strong>Install AniSync</strong>' +
                '<span>Tap <span aria-hidden="true">⬆️</span> Share, then <em>Add to Home Screen</em>.</span>' +
            '</div>' +
            '<button class="pwa-ios-hint-close" aria-label="Dismiss">×</button>';
        document.body.appendChild(hint);
        hint.querySelector('.pwa-ios-hint-close').addEventListener('click', function () {
            hint.remove();
            try { localStorage.setItem('anisync-pwa-ios-dismissed', '1'); }
            catch (e) { /* ignore */ }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', maybeShowIosHint);
    } else {
        maybeShowIosHint();
    }
})();
