// Theme toggle — cycles System → Light → Dark and persists the choice.
//
// The pre-paint stamp in _Layout's <head> has already applied a stored
// light/dark preference to <html data-theme> before first paint (so there's
// no flash); this module owns everything *after* load: wiring the toggle
// button(s), swapping the displayed icon, persisting to localStorage, and
// keeping the OS chrome's theme-color in sync.
//
// Theme resolution (mirrors the CSS in site.css):
//   • "system" → no data-theme attribute; prefers-color-scheme drives it
//   • "light"  → data-theme="light" (overrides the OS)
//   • "dark"   → data-theme="dark"  (overrides the OS)
//
// theme-color sync: _Layout ships two media-scoped <meta name="theme-color">
// tags that handle the system-follows case. When the user makes a MANUAL
// choice we append a single media-less meta (always matches, last-in-DOM
// wins) carrying the resolved color; in "system" mode we remove it so the
// media tags take back over.
(function () {
    'use strict';

    var STORAGE_KEY = 'anisync-theme';
    var ORDER = ['system', 'light', 'dark'];
    // theme-color values per resolved scheme — kept in step with the manifest
    // background_color (dark) and the light theme's --bg.
    var COLOR = { dark: '#0A0A0A', light: '#ffffff' };

    var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

    function getStored() {
        try {
            var v = localStorage.getItem(STORAGE_KEY);
            return (v === 'light' || v === 'dark') ? v : 'system';
        } catch (_) { return 'system'; }
    }

    function setStored(mode) {
        try {
            if (mode === 'system') localStorage.removeItem(STORAGE_KEY);
            else localStorage.setItem(STORAGE_KEY, mode);
        } catch (_) { /* storage blocked — in-memory only for this session */ }
    }

    // The scheme actually showing on screen, accounting for "system".
    function resolved(mode) {
        if (mode === 'light' || mode === 'dark') return mode;
        return (media && media.matches) ? 'dark' : 'light';
    }

    function applyAttr(mode) {
        var root = document.documentElement;
        if (mode === 'system') root.removeAttribute('data-theme');
        else root.setAttribute('data-theme', mode);
    }

    // Authoritative media-less theme-color meta for manual choices; absent in
    // system mode so the two media-scoped metas drive the chrome instead.
    function syncThemeColor(mode) {
        var existing = document.querySelector('meta[name="theme-color"]:not([media])');
        if (mode === 'system') {
            if (existing) existing.remove();
            return;
        }
        if (!existing) {
            existing = document.createElement('meta');
            existing.setAttribute('name', 'theme-color');
            document.head.appendChild(existing);
        }
        existing.setAttribute('content', COLOR[resolved(mode)]);
    }

    function syncButtons(mode) {
        var label = 'Theme: ' + mode;
        document.querySelectorAll('[data-theme-toggle]').forEach(function (btn) {
            btn.setAttribute('aria-label', label);
            btn.setAttribute('title', label);
            btn.querySelectorAll('[data-theme-icon]').forEach(function (icon) {
                icon.hidden = icon.getAttribute('data-theme-icon') !== mode;
            });
        });
    }

    function apply(mode) {
        applyAttr(mode);
        syncThemeColor(mode);
        syncButtons(mode);
    }

    var current = getStored();
    apply(current);

    function cycle() {
        current = ORDER[(ORDER.indexOf(current) + 1) % ORDER.length];
        setStored(current);
        apply(current);
    }

    document.querySelectorAll('[data-theme-toggle]').forEach(function (btn) {
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            cycle();
        });
    });

    // When following the OS and the OS flips, re-sync the theme-color meta
    // (the CSS already reacts on its own via prefers-color-scheme).
    if (media) {
        var onChange = function () { if (current === 'system') syncThemeColor('system'); };
        if (media.addEventListener) media.addEventListener('change', onChange);
        else if (media.addListener) media.addListener(onChange); // Safari < 14
    }
})();
