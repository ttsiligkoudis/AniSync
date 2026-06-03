// Shared chrome behaviour for the AniSync MAUI/Web client. Uses document-level
// event delegation (not load-time querySelector) so it works regardless of when
// Blazor mounts the layout components. Mirrors the inline scripts + theme-
// toggle.js / site-back behaviour from the MVC _Layout.cshtml.
(function () {
    'use strict';

    var THEME_KEY = 'anisync-theme';

    // ---- Theme (light/dark) ---------------------------------------------------
    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        var meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.setAttribute('content', theme === 'light' ? '#ffffff' : '#0b0d12');
    }
    function currentTheme() {
        return document.documentElement.getAttribute('data-theme')
            || (window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
    }
    try {
        var saved = localStorage.getItem(THEME_KEY);
        if (saved) applyTheme(saved);
    } catch (e) { /* storage blocked — fall back to CSS prefers-color-scheme */ }

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
        // Theme toggle.
        if (e.target.closest('[data-theme-toggle]')) {
            var next = currentTheme() === 'light' ? 'dark' : 'light';
            applyTheme(next);
            try { localStorage.setItem(THEME_KEY, next); } catch (err) { /* ignore */ }
            return;
        }

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
