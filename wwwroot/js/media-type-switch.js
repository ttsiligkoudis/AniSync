// Fixed media-type switch (anime / movies / series) below the header.
//
//  • Dashboard (data-mt-switch="dashboard"): an "All" pill + one per enabled
//    mode. Filters the dashboard's [data-media-type] sections client-side —
//    no reload. Picking a concrete mode also becomes the active mode for
//    Discover / Library so the surfaces stay in step.
//  • Discover / Library (data-mt-switch="navigate"): switching writes the
//    active mode (localStorage + cookie, the latter drives SSR) and reloads
//    into that mode.
//
// The selected value persists in localStorage.
(function () {
    'use strict';

    var ACTIVE_KEY = 'anisync-media-type';        // active mode (Discover/Library)
    var DASH_KEY = 'anisync-dashboard-media';      // dashboard filter (all|anime|movie|series)
    var ACTIVE_COOKIE = 'anisync_media_type';      // SSR reads this for the active mode
    var VALID = ['anime', 'movie', 'series'];

    var bar = document.querySelector('[data-mt-switch]');
    if (!bar) return;
    var mode = bar.getAttribute('data-mt-switch');
    var btns = Array.prototype.slice.call(bar.querySelectorAll('.mt-switch-btn'));
    if (btns.length === 0) return;

    function read(k) { try { return localStorage.getItem(k); } catch (_) { return null; } }
    function write(k, v) { try { localStorage.setItem(k, v); } catch (_) { /* private mode */ } }
    function setCookie(n, v) {
        try { document.cookie = n + '=' + encodeURIComponent(v) + '; path=/; max-age=31536000; SameSite=Lax'; }
        catch (_) { /* cookies blocked */ }
    }
    function has(val) { return btns.some(function (b) { return b.getAttribute('data-mt-value') === val; }); }

    function setActive(val) {
        btns.forEach(function (b) {
            var on = b.getAttribute('data-mt-value') === val;
            b.classList.toggle('active', on);
            b.setAttribute('aria-selected', on ? 'true' : 'false');
        });
    }

    // ── Keep the bar pinned just below the (sticky) header ──────────────
    var header = document.querySelector('.site-header');
    function positionBar() {
        if (!header) return;
        bar.style.top = header.offsetHeight + 'px';
    }
    positionBar();
    window.addEventListener('resize', positionBar);

    if (mode === 'dashboard') {
        var sections = Array.prototype.slice.call(document.querySelectorAll('[data-media-type]'));
        function applyFilter(filter) {
            sections.forEach(function (s) {
                var types = (s.getAttribute('data-media-type') || '').split(/\s+/);
                var show = filter === 'all' || types.indexOf(filter) !== -1;
                s.classList.toggle('mt-filter-hidden', !show);
            });
        }

        var filter = read(DASH_KEY) || 'all';
        if (!has(filter)) filter = 'all';
        setActive(filter);
        applyFilter(filter);

        btns.forEach(function (b) {
            b.addEventListener('click', function () {
                var v = b.getAttribute('data-mt-value');
                write(DASH_KEY, v);
                setActive(v);
                applyFilter(v);
                // Picking a concrete mode also sets the active mode the
                // Discover / Library surfaces render, so they follow along.
                if (VALID.indexOf(v) !== -1) {
                    write(ACTIVE_KEY, v);
                    setCookie(ACTIVE_COOKIE, v);
                }
            });
        });
    } else {
        // navigate: SSR already highlights the active mode; a click switches
        // it and reloads so the server renders the chosen mode.
        btns.forEach(function (b) {
            b.addEventListener('click', function () {
                var v = b.getAttribute('data-mt-value');
                if (b.classList.contains('active') || VALID.indexOf(v) === -1) return;
                write(ACTIVE_KEY, v);
                setCookie(ACTIVE_COOKIE, v);
                write(DASH_KEY, v);   // keep the dashboard filter in step
                location.reload();
            });
        });
    }
})();
