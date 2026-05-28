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
    // Chromium requires an active SW. Failure here doesn't break
    // anything user-facing (the site just isn't installable).
    //
    // Update flow: the SW no longer skipWaiting()s on install, so a
    // freshly-deployed worker parks in `waiting` instead of swapping
    // assets out from under the running page. We watch for that waiting
    // worker and surface a tasteful "New version available" toast; on
    // tap we post SKIP_WAITING and reload once the new worker takes
    // control (controllerchange). This kills the "app looks broken until
    // I refresh after an update" glitch.
    if ('serviceWorker' in navigator) {
        var reloadingForUpdate = false;
        // When the freshly-activated worker takes control, reload exactly
        // once so the page picks up the new CSS/JS cleanly.
        navigator.serviceWorker.addEventListener('controllerchange', function () {
            if (reloadingForUpdate) return;
            reloadingForUpdate = true;
            window.location.reload();
        });

        var promptShownFor = null; // de-dupe the toast per waiting worker

        function promptUpdate(waitingWorker) {
            if (!waitingWorker || promptShownFor === waitingWorker) return;
            promptShownFor = waitingWorker;
            var toast = window.AniSyncToast;
            var activate = function () {
                waitingWorker.postMessage({ type: 'SKIP_WAITING' });
            };
            if (toast && toast.show) {
                toast.show('New version available', {
                    action: 'Update',
                    onAction: activate,
                    persist: true
                });
            } else {
                // No toast component on this page — activate silently; the
                // controllerchange reload still gives a clean swap.
                activate();
            }
        }

        window.addEventListener('load', function () {
            navigator.serviceWorker.register('/sw.js').then(function (reg) {
                // A worker may already be waiting (update downloaded while the
                // page was closed, then reopened from cache).
                if (reg.waiting && navigator.serviceWorker.controller) {
                    promptUpdate(reg.waiting);
                }
                // A new worker started installing for this page session.
                reg.addEventListener('updatefound', function () {
                    var installing = reg.installing;
                    if (!installing) return;
                    installing.addEventListener('statechange', function () {
                        // `installed` + an existing controller == an update
                        // (not the very first install on a fresh client).
                        if (installing.state === 'installed' && navigator.serviceWorker.controller) {
                            promptUpdate(reg.waiting || installing);
                        }
                    });
                });
            }).catch(function () {
                // ignore — falls back to a non-installable site
            });
        });
    }

    // Every element marked data-pwa-install gets toggled together — the
    // layout now has two (the header pill on desktop, the bottom-nav More
    // popup on mobile) and both should reveal / hide in lockstep so the
    // chrome stays consistent across breakpoints.
    var installBtns = Array.prototype.slice.call(document.querySelectorAll('[data-pwa-install]'));
    var deferredPrompt = null;

    function setInstallVisible(visible) {
        installBtns.forEach(function (b) { b.hidden = !visible; });
    }

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
        if (installBtns.length && !isStandalone()) {
            setInstallVisible(true);
        }
    });

    installBtns.forEach(function (btn) {
        btn.addEventListener('click', function () {
            if (!deferredPrompt) return;
            deferredPrompt.prompt();
            deferredPrompt.userChoice.then(function () {
                // Hide regardless — the prompt can only fire once per
                // event instance. The appinstalled handler hides on
                // accept; for a dismissed prompt the buttons stay hidden
                // until the next pageload (deferredPrompt is exhausted).
                setInstallVisible(false);
                deferredPrompt = null;
            });
        });
    });

    window.addEventListener('appinstalled', function () {
        setInstallVisible(false);
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
