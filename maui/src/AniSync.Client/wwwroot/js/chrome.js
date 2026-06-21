// Shared chrome behaviour for the AniSync MAUI/Web client. Uses document-level
// event delegation (not load-time querySelector) so it works regardless of when
// Blazor mounts the layout components. Mirrors the inline scripts + theme-
// toggle.js / site-back behaviour from the MVC _Layout.cshtml.
(function () {
    'use strict';

    // ---- Theme (System / Light / Dark) ---------------------------------------
    // The theme preference now lives in the app's settings store (ISecureStore) and
    // is applied by MainLayout on startup + the Account page's switch — not a header
    // toggle or a localStorage key. This just exposes the apply function .NET calls.
    // 'light'/'dark' pin data-theme; anything else ('system') clears it so the CSS
    // follows the OS via prefers-color-scheme. theme-bridge.js observes data-theme
    // and re-tints the native status bars.
    window.anisyncApplyTheme = function (theme) {
        var meta = document.querySelector('meta[name="theme-color"]');
        if (theme === 'light' || theme === 'dark') {
            document.documentElement.setAttribute('data-theme', theme);
            if (meta) meta.setAttribute('content', theme === 'light' ? '#ffffff' : '#0b0d12');
        } else {
            document.documentElement.removeAttribute('data-theme');
            if (meta) {
                var dark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
                meta.setAttribute('content', dark ? '#0b0d12' : '#ffffff');
            }
        }
    };

    // ---- "More" sheet (mobile bottom nav) ------------------------------------
    function moreEls() {
        return {
            toggle: document.querySelector('[data-bottom-nav-more-toggle]'),
            sheet: document.getElementById('bottom-nav-more-dome'),
        };
    }
    function closeMore() {
        var m = moreEls();
        if (!m.sheet || !m.toggle) return;
        m.sheet.hidden = true;
        document.body.classList.remove('bottom-nav-more-open');
        m.toggle.setAttribute('aria-expanded', 'false');
        m.toggle.classList.remove('is-open');
        var label = m.toggle.querySelector('[data-more-label]');
        if (label) label.textContent = 'More';
        m.toggle.setAttribute('aria-label', 'More');
    }
    function openMore() {
        var m = moreEls();
        if (!m.sheet || !m.toggle) return;
        m.sheet.hidden = false;
        document.body.classList.add('bottom-nav-more-open');
        m.toggle.setAttribute('aria-expanded', 'true');
        m.toggle.classList.add('is-open');
        var label = m.toggle.querySelector('[data-more-label]');
        if (label) label.textContent = 'Close';
        m.toggle.setAttribute('aria-label', 'Close menu');
    }

    // ---- Delegated click handling --------------------------------------------
    document.addEventListener('click', function (e) {
        // Back button — history.back() with a cold-load fallback to "/".
        if (e.target.closest('[data-site-back]')) {
            var hadHistory = window.history.length > 1;
            window.history.back();
            if (!hadHistory) setTimeout(function () { window.location.href = '/'; }, 500);
            return;
        }

        // More-sheet toggle.
        var toggle = e.target.closest('[data-bottom-nav-more-toggle]');
        if (toggle) {
            e.stopPropagation();
            var m = moreEls();
            if (m.sheet && m.sheet.hidden) openMore(); else closeMore();
            return;
        }

        // Tapping a row navigates, then closes the sheet.
        if (e.target.closest('.bottom-nav-more-row')) { closeMore(); return; }

        // Outside click closes the sheet.
        var sheet = document.getElementById('bottom-nav-more-dome');
        if (sheet && !sheet.hidden && !e.target.closest('#bottom-nav-more-dome')) closeMore();
    });

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;
        var sheet = document.getElementById('bottom-nav-more-dome');
        if (sheet && !sheet.hidden) closeMore();
        document.body.classList.remove('drawer-open');
    });
})();
