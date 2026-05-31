// Media-type preference modal. Drives the whole web UI's anime / movies /
// series mode. On first visit (no stored choice) it forces a selection; a
// [data-media-type-open] trigger in the chrome reopens it on demand. The pick
// is written to localStorage (cross-page memory) AND a cookie (so server-side
// rendering honours it, including for anonymous visitors), persisted to the
// account setting when logged in, then the page reloads so every surface
// re-renders for the chosen mode.
(function () {
    var STORAGE_KEY = 'anisync-media-type';
    var COOKIE = 'anisync_media_type';

    var modal = document.querySelector('[data-media-type-modal]');
    if (!modal) return;
    var backdrop = document.querySelector('[data-media-type-backdrop]');
    var closeBtn = modal.querySelector('[data-media-type-close]');
    var loggedIn = modal.getAttribute('data-logged-in') === 'true';

    function valid(v) { return v === 'anime' || v === 'movie' || v === 'series'; }
    function read() { try { return localStorage.getItem(STORAGE_KEY); } catch (_) { return null; } }
    function setCookie(v) {
        try { document.cookie = COOKIE + '=' + v + '; path=/; max-age=31536000; SameSite=Lax'; }
        catch (_) { /* cookies blocked — SSR falls back to anime */ }
    }
    function codeOf(v) { return v === 'movie' ? 1 : v === 'series' ? 2 : 0; }

    function open(forced) {
        modal.hidden = false;
        if (backdrop) backdrop.hidden = false;
        document.body.classList.add('mt-modal-open');
        // First-visit selection is mandatory — hide the dismiss affordance so
        // the only way out is choosing a mode. Reopen (forced=false) shows it.
        modal.setAttribute('data-forced', forced ? 'true' : 'false');
        if (closeBtn) closeBtn.hidden = !!forced;
    }
    function close() {
        if (modal.getAttribute('data-forced') === 'true') return;
        modal.hidden = true;
        if (backdrop) backdrop.hidden = true;
        document.body.classList.remove('mt-modal-open');
    }

    async function choose(v) {
        if (!valid(v)) return;
        try { localStorage.setItem(STORAGE_KEY, v); } catch (_) { /* private mode */ }
        setCookie(v);

        // Drop media-type-specific client caches so the reload paints the new
        // mode's data instead of the previous mode's cached shelves.
        try {
            localStorage.removeItem('anisync.continueWatching.v1');
            localStorage.removeItem('anisync.videoContinueWatching.v1');
        } catch (_) { /* ignore */ }

        if (loggedIn) {
            // Persist to the account setting so the choice follows the user
            // across devices. CsrfOrAjaxFilter accepts same-origin AJAX via the
            // X-Requested-With header, so no antiforgery token plumbing needed.
            try {
                var body = 'mediaType=' + codeOf(v);
                await fetch('/Home/SetMediaType', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: {
                        'Content-Type': 'application/x-www-form-urlencoded',
                        'X-Requested-With': 'XMLHttpRequest'
                    },
                    body: body,
                    skipLoader: true
                });
            } catch (_) { /* best-effort — the cookie still drives SSR */ }
        }

        location.reload();
    }

    modal.querySelectorAll('[data-media-type-choice]').forEach(function (btn) {
        btn.addEventListener('click', function () { choose(btn.getAttribute('data-media-type-choice')); });
    });
    if (closeBtn) closeBtn.addEventListener('click', close);
    if (backdrop) backdrop.addEventListener('click', close);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });

    // Reopen triggers anywhere in the site chrome.
    document.querySelectorAll('[data-media-type-open]').forEach(function (el) {
        el.addEventListener('click', function (e) { e.preventDefault(); open(false); });
    });

    // First visit (no stored choice) → force a selection. Otherwise keep the
    // SSR cookie in step with localStorage (covers a cleared cookie, or a
    // value chosen before the cookie mechanism existed).
    var stored = read();
    if (!valid(stored)) {
        open(true);
    } else {
        setCookie(stored);
    }
})();
