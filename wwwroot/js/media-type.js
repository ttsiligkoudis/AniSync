// Media-type preference modal (MULTI-SELECT). Drives the web UI's anime /
// movies / series modes:
//   • the ENABLED SET — one or more modes the user picks here. The dashboard
//     combines shelves across them; Discover / Library only offer these.
//   • the ACTIVE mode — the single mode Discover / Library currently render.
//     Clamped to stay within the enabled set.
// On first visit (no stored set) the modal forces a selection; a
// [data-media-type-open] trigger in the chrome reopens it. Choices are written
// to localStorage AND cookies (so SSR honours them, anonymous included) — there
// is no account/DB setting — then the page reloads so every surface re-renders.
(function () {
    var SET_KEY = 'anisync-media-types';   // enabled set (comma list)
    var ACTIVE_KEY = 'anisync-media-type';  // active single
    var SET_COOKIE = 'anisync_media_types';
    var ACTIVE_COOKIE = 'anisync_media_type';
    var ALL = ['anime', 'movie', 'series'];

    var modal = document.querySelector('[data-media-type-modal]');
    if (!modal) return;
    var backdrop = document.querySelector('[data-media-type-backdrop]');
    var closeBtn = modal.querySelector('[data-media-type-close]');
    var confirmBtn = modal.querySelector('[data-media-type-confirm]');
    var hint = modal.querySelector('[data-media-type-hint]');
    var options = Array.prototype.slice.call(modal.querySelectorAll('[data-media-type-choice]'));

    function valid(v) { return ALL.indexOf(v) !== -1; }
    function ordered(list) { return ALL.filter(function (t) { return list.indexOf(t) !== -1; }); }
    function readSet() {
        try {
            var raw = localStorage.getItem(SET_KEY);
            if (!raw) return [];
            return ordered(raw.split(',').filter(valid));
        } catch (_) { return []; }
    }
    function readActive() { try { return localStorage.getItem(ACTIVE_KEY); } catch (_) { return null; } }
    function readCookie(name) {
        try {
            var m = document.cookie.match(new RegExp('(?:^|; )' + name + '=([^;]*)'));
            return m ? decodeURIComponent(m[1]) : null;
        } catch (_) { return null; }
    }
    function setCookie(name, v) {
        // URL-encode the value: the enabled set is comma-joined, and ASP.NET
        // Core's request-cookie parser treats a raw comma as a cookie separator
        // (it would read only the first mode). encodeURIComponent turns the
        // commas into %2C; the server URL-decodes on read.
        try { document.cookie = name + '=' + encodeURIComponent(v) + '; path=/; max-age=31536000; SameSite=Lax'; }
        catch (_) { /* cookies blocked — SSR falls back to anime */ }
    }
    // ── selection state (reflected by .is-selected on the option buttons) ──
    function selected() {
        return ordered(options
            .filter(function (b) { return b.classList.contains('is-selected'); })
            .map(function (b) { return b.getAttribute('data-media-type-choice'); }));
    }
    function syncConfirm() {
        var any = selected().length > 0;
        if (confirmBtn) confirmBtn.disabled = !any;
        if (hint) hint.hidden = any;
    }
    function mark(btn, on) {
        btn.classList.toggle('is-selected', on);
        btn.setAttribute('aria-pressed', on ? 'true' : 'false');
    }
    function preselect(set) {
        options.forEach(function (b) {
            mark(b, set.indexOf(b.getAttribute('data-media-type-choice')) !== -1);
        });
        syncConfirm();
    }

    function open(forced) {
        preselect(readSet());
        modal.hidden = false;
        if (backdrop) backdrop.hidden = false;
        document.body.classList.add('mt-modal-open');
        modal.setAttribute('data-forced', forced ? 'true' : 'false');
        if (closeBtn) closeBtn.hidden = !!forced;
    }
    function close() {
        if (modal.getAttribute('data-forced') === 'true') return;
        modal.hidden = true;
        if (backdrop) backdrop.hidden = true;
        document.body.classList.remove('mt-modal-open');
    }

    function confirm() {
        var set = selected();
        if (set.length === 0) { syncConfirm(); return; }

        // Active mode: keep the current one if it's still enabled, else the
        // first of the new set.
        var active = readActive();
        if (!valid(active) || set.indexOf(active) === -1) active = set[0];

        try {
            localStorage.setItem(SET_KEY, set.join(','));
            localStorage.setItem(ACTIVE_KEY, active);
        } catch (_) { /* private mode */ }
        setCookie(SET_COOKIE, set.join(','));
        setCookie(ACTIVE_COOKIE, active);

        // Drop media-type-specific caches so the reload paints the new modes.
        try {
            localStorage.removeItem('anisync.continueWatching.v1');
            localStorage.removeItem('anisync.videoContinueWatching.v1');
        } catch (_) { /* ignore */ }

        // Persist to the account too (logged-in) so the choice follows the user
        // across devices. SSR prefers the ACCOUNT setting over the cookie for
        // logged-in users, so we must WAIT for the save before reloading —
        // otherwise the reload races the POST and the server re-renders the
        // stale set (the bug where a newly-added mode only showed after a
        // second manual refresh). Anonymous users are cookie-only, so they
        // reload immediately.
        if (window.AniSync && window.AniSync.loggedIn) {
            if (confirmBtn) confirmBtn.disabled = true;
            fetch('/Home/SetEnabledMediaTypes', {
                method: 'POST',
                credentials: 'same-origin',
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'X-Requested-With': 'XMLHttpRequest'
                },
                body: 'enabled=' + encodeURIComponent(set.join(',')),
                skipLoader: true
            })
            .catch(function () { /* save failed — reload anyway, the cookie is set */ })
            .then(function () { location.reload(); });
        } else {
            location.reload();
        }
    }

    options.forEach(function (btn) {
        btn.addEventListener('click', function () {
            mark(btn, !btn.classList.contains('is-selected'));
            syncConfirm();
        });
    });
    if (confirmBtn) confirmBtn.addEventListener('click', confirm);
    if (closeBtn) closeBtn.addEventListener('click', close);
    if (backdrop) backdrop.addEventListener('click', close);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });

    document.querySelectorAll('[data-media-type-open]').forEach(function (el) {
        el.addEventListener('click', function (e) { e.preventDefault(); open(false); });
    });

    // Account settings (logged-in) are the source of truth: seed localStorage +
    // the cookie from them so the choice follows the user across devices. The
    // server also resolves SSR from the account, so this just keeps the client
    // (modal preselect, first-visit check) in step.
    var boot = (window.AniSync && window.AniSync.settings) || null;
    if (boot && boot.enabledMediaTypes) {
        var dbSet = ordered(String(boot.enabledMediaTypes).split(',').filter(valid));
        if (dbSet.length) {
            try { localStorage.setItem(SET_KEY, dbSet.join(',')); } catch (_) { /* private mode */ }
            setCookie(SET_COOKIE, dbSet.join(','));
        }
    }

    // First visit (no stored set) → forced selection. Otherwise keep the SSR
    // cookies in step with localStorage (covers cleared cookies / older builds).
    var storedSet = readSet();
    if (storedSet.length === 0) {
        open(true);
    } else {
        // The enabled set is only ever changed by the modal, so keep its cookie
        // in sync with localStorage (covers a cleared cookie + the comma fix).
        setCookie(SET_COOKIE, storedSet.join(','));
        // The ACTIVE mode is owned by the Discover/Library toggle (a server-set
        // cookie) and the modal. Mirror the cookie's value INTO localStorage so
        // the modal reflects toggle switches — but never write the cookie FROM
        // localStorage here: that overwrote the toggle's choice on the next
        // navigation, making a list-tab click revert the media type.
        var cookieActive = readCookie(ACTIVE_COOKIE);
        if (valid(cookieActive)) {
            try { localStorage.setItem(ACTIVE_KEY, cookieActive); } catch (_) { /* private mode */ }
        }
    }
})();
