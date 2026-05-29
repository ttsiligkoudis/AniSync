// Theme toggle — a simple two-state light ⇄ dark switch.
//
// Each tap flips to the opposite of whatever is currently showing and stores
// the explicit choice, so every tap visibly changes the theme. (The previous
// three-state System → Light → Dark cycle could land on a state that resolved
// to the same effective theme as the OS, which read as "nothing happened" for
// a tap or two.)
//
// Until the user taps, no preference is stored and the CSS follows the OS via
// prefers-color-scheme (the pre-paint stamp in _Layout only sets data-theme
// when an explicit light/dark choice exists). The icon reflects the CURRENT
// theme and implies the action: a sun while dark (tap → light), a moon while
// light (tap → dark).
(function () {
    'use strict';

    var STORAGE_KEY = 'anisync-theme';
    // theme-color values per scheme — kept in step with the manifest
    // background_color (dark) and the light theme's --bg.
    var COLOR = { dark: '#0A0A0A', light: '#ffffff' };

    var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

    function stored() {
        try {
            var v = localStorage.getItem(STORAGE_KEY);
            return (v === 'light' || v === 'dark') ? v : null;
        } catch (_) { return null; }
    }

    // The scheme actually on screen: an explicit choice, else the OS.
    function effective() {
        return stored() || ((media && media.matches) ? 'dark' : 'light');
    }

    // Authoritative media-less theme-color meta for explicit choices (a
    // media-less meta always matches and, being last in DOM order, wins over
    // the two media-scoped metas _Layout ships).
    function syncMeta(theme) {
        var m = document.querySelector('meta[name="theme-color"]:not([media])');
        if (!m) {
            m = document.createElement('meta');
            m.setAttribute('name', 'theme-color');
            document.head.appendChild(m);
        }
        m.setAttribute('content', COLOR[theme]);
    }

    // The sun/moon icon is driven by CSS off the active theme (see site.css),
    // so JS only keeps the accessible label in step with the action.
    function syncLabel(theme) {
        var next = theme === 'dark' ? 'light' : 'dark';
        document.querySelectorAll('[data-theme-toggle]').forEach(function (btn) {
            var label = 'Switch to ' + next + ' theme';
            btn.setAttribute('aria-label', label);
            btn.setAttribute('title', label);
        });
    }

    function applyExplicit(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        syncMeta(theme);
        syncLabel(theme);
    }

    // Initial render: reflect whatever is showing. Only touch the theme-color
    // meta when there's an explicit choice (otherwise leave the media metas to
    // follow the OS).
    syncLabel(effective());
    if (stored()) syncMeta(effective());

    function toggle() {
        var next = effective() === 'dark' ? 'light' : 'dark';
        try { localStorage.setItem(STORAGE_KEY, next); } catch (_) { /* storage blocked */ }
        applyExplicit(next);
    }

    document.querySelectorAll('[data-theme-toggle]').forEach(function (btn) {
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            toggle();
        });
    });

    // While still following the OS (no explicit choice), keep the icon in step
    // if the OS theme flips. The CSS reacts on its own via prefers-color-scheme.
    if (media) {
        var onChange = function () { if (!stored()) syncLabel(effective()); };
        if (media.addEventListener) media.addEventListener('change', onChange);
        else if (media.addListener) media.addListener(onChange); // Safari < 14
    }
})();
