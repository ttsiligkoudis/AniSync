// Bell + dropdown for per-user episode notifications in the site header.
//
// Refresh strategy: the /count endpoint returns `{count, nextAiringAt}`.
// nextAiringAt is the Unix-seconds timestamp of the next future episode
// matching the user's Watching list (precomputed by the dispatcher every
// 5 min). The bell schedules a single setTimeout to (nextAiringAt + cron
// grace) instead of polling — a user with one show airing tomorrow makes
// one count request per day, not 1,440. When nothing's scheduled in the
// lookahead window we fall back to an hourly tick so the user's library
// changes (adding new "Watching" anime) eventually surface.
//
// Click of the bell expands the dropdown, which fetches the most recent
// 20 notifications and renders each as an <a> linking to the episode's
// watch page. Clicking a row fires a fire-and-forget POST /{id}/read
// (with keepalive so the marker survives navigation).
(function () {
    'use strict';

    var COUNT_URL = '/api/v1/notifications/count';
    var LIST_URL = '/api/v1/notifications?limit=10';
    // Wait this long after a known airingAt before refreshing. The cron
    // tick runs every 5 min server-side, so by airingAt + ~6 min the
    // notification row should exist in the DB.
    var POST_AIRING_GRACE_MS = 6 * 60 * 1000;
    // Lookahead window the dispatcher pulls is 24h; outside that range
    // nextAiringAt is null and we fall back to this idle interval so a
    // newly-added watching entry eventually shows up.
    var IDLE_REFRESH_MS = 60 * 60 * 1000;
    // After a network error, retry sooner than the idle interval but not
    // so fast we hammer a failing endpoint.
    var ERROR_RETRY_MS = 5 * 60 * 1000;
    // Floor so a "fire immediately" target (airingAt already in the past
    // when the page loads) still leaves the dispatcher a beat to catch up
    // and doesn't loop tightly if responses keep coming back stale.
    var MIN_DELAY_MS = 30 * 1000;

    var bell = document.querySelector('[data-notif-bell]');
    if (!bell) return;
    var toggle = bell.querySelector('[data-notif-toggle]');
    var panel = bell.querySelector('#notif-panel');
    var badge = bell.querySelector('[data-notif-count]');
    var list = bell.querySelector('[data-notif-list]');
    var emptyState = bell.querySelector('[data-notif-empty]');
    var markAllBtn = bell.querySelector('[data-notif-read-all]');
    if (!toggle || !panel || !badge || !list) return;

    // Reparent the panel to <body>. It's authored inside .notif-bell (which
    // lives in .site-header: sticky + z-index:50, i.e. its own stacking
    // context), so the panel's z-index is otherwise resolved *within* that
    // context and capped at the header's level — letting the watch page's
    // video player paint over it. position:fixed does NOT escape an ancestor
    // stacking context for paint order; only reparenting does. As a direct
    // child of <body> the panel's z-index finally competes at the document
    // root and sits above the player. (Its toggle/positioning logic keys off
    // the bell's screen rect, so the visual anchor is unaffected.)
    if (panel.parentElement !== document.body) document.body.appendChild(panel);

    var listLoaded = false;
    var pollTimer = null;

    function setBadge(count) {
        var n = Number(count) || 0;
        badge.textContent = n > 99 ? '99+' : String(n);
        badge.hidden = n === 0;
    }

    function scheduleNext(delayMs) {
        if (pollTimer) clearTimeout(pollTimer);
        // setTimeout caps at ~24.8 days (2^31 ms). Our IDLE_REFRESH_MS and
        // the 24h lookahead window are both well under that, but clamp
        // defensively in case the server hands back a far-future value.
        var capped = Math.min(Math.max(delayMs, MIN_DELAY_MS), 24 * 60 * 60 * 1000);
        pollTimer = setTimeout(refreshCount, capped);
    }

    async function refreshCount() {
        if (document.hidden) {
            // Defer the work until the tab is visible again; the
            // visibilitychange handler below restarts the loop.
            return;
        }
        try {
            // skipLoader: bell refresh is a background poll that fires on a
            // schedule the user didn't initiate — flashing the global
            // spinner for it every wake would be noisy. Same opt-out the
            // typeahead search uses (see loader.js).
            var res = await fetch(COUNT_URL, { credentials: 'same-origin', skipLoader: true });
            if (!res.ok) {
                scheduleNext(ERROR_RETRY_MS);
                return;
            }
            var data = await res.json();
            var newCount = Number(data && data.count) || 0;
            // Read the previous count BEFORE updating the badge so we can
            // detect new arrivals — "99+" parses as 99 which is good
            // enough for the > comparison (the rare case of 99→100 still
            // shows as "99+" and the user wouldn't notice a stale list
            // beyond that count anyway).
            var prevCount = Number((badge.textContent || '').replace('+', '')) || 0;
            setBadge(newCount);

            if (newCount > prevCount) {
                // A new notification was inserted server-side since the last
                // refresh. The dropdown's cached list (loaded lazily on the
                // first bell click) is now stale — invalidate it so the next
                // panel open refetches. If the panel is already open right
                // now, refetch immediately so the new row appears without
                // waiting for the user to close + reopen.
                listLoaded = false;
                if (!panel.hidden) loadList();
            }

            if (data && data.nextAiringAt) {
                // Schedule one timeout for the precise airing time (+
                // grace for the cron tick to fire and persist the row).
                // Re-running refresh after that point will get a fresh
                // nextAiringAt advanced to the *next* future airing.
                var targetMs = Number(data.nextAiringAt) * 1000 + POST_AIRING_GRACE_MS;
                scheduleNext(targetMs - Date.now());
            } else {
                // Nothing airing in the 24h lookahead — fall back to a
                // periodic check so library changes (adding a new watching
                // entry) eventually surface in the bell.
                scheduleNext(IDLE_REFRESH_MS);
            }
        } catch (_) {
            scheduleNext(ERROR_RETRY_MS);
        }
    }

    function relativeTime(unix) {
        var nowSec = Math.floor(Date.now() / 1000);
        var diff = nowSec - Number(unix);
        if (!isFinite(diff)) return '';
        if (diff < 60) return 'just now';
        if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
        if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
        return Math.floor(diff / 86400) + 'd ago';
    }

    function escape(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function renderItems(items) {
        if (!items || items.length === 0) {
            list.innerHTML = '';
            if (emptyState) {
                emptyState.hidden = false;
                list.appendChild(emptyState);
            }
            return;
        }
        if (emptyState) emptyState.hidden = true;

        var html = items.map(function (item) {
            var unread = item.readAt == null;
            var thumb = item.thumbnailUrl
                ? '<img class="notif-thumb" src="' + escape(item.thumbnailUrl) + '" alt="" loading="lazy" />'
                : '<div class="notif-thumb notif-thumb-placeholder" aria-hidden="true"></div>';
            return '<a class="notif-item' + (unread ? ' notif-item-unread' : '') + '"'
                + ' href="' + escape(item.linkPath) + '"'
                + ' data-notif-id="' + escape(item.id) + '">'
                + thumb
                + '<div class="notif-body">'
                + '<div class="notif-title">' + escape(item.animeTitle) + '</div>'
                + '<div class="notif-meta">Episode ' + escape(item.episodeNumber) + ' &middot; ' + escape(relativeTime(item.createdAt)) + '</div>'
                + '</div>'
                + '</a>';
        }).join('');
        list.innerHTML = html;
    }

    async function loadList() {
        try {
            var res = await fetch(LIST_URL, { credentials: 'same-origin', skipLoader: true });
            if (!res.ok) return;
            var data = await res.json();
            renderItems(data && data.items);
            listLoaded = true;
        } catch (_) { /* keep last state — user can retry by reopening */ }
    }

    // The panel is position:fixed and portaled to <body> (see init), so it's
    // anchored to the viewport rather than the bell. At desktop widths the
    // visual "panel hangs below the bell" anchor is therefore no longer free
    // from CSS, so we measure the bell's screen rect and pin top/right inline.
    function positionPanel() {
        // ≤600px: full-width via media query — clear any inline overrides.
        if (window.matchMedia('(max-width: 600px)').matches) {
            panel.style.top = '';
            panel.style.left = '';
            panel.style.right = '';
            return;
        }
        var rect = toggle.getBoundingClientRect();
        panel.style.top = (rect.bottom + 8) + 'px';
        panel.style.right = Math.max(8, window.innerWidth - rect.right) + 'px';
        panel.style.left = 'auto';
    }

    function openPanel() {
        panel.hidden = false;
        positionPanel();
        toggle.setAttribute('aria-expanded', 'true');
        bell.classList.add('notif-bell-open');
        // Lazy-load on first open; subsequent opens reuse the rendered list
        // and rely on the scheduled refresh for new counts. The user can
        // close and reopen to force a fresh fetch.
        if (!listLoaded) loadList();
    }
    function closePanel() {
        panel.hidden = true;
        toggle.setAttribute('aria-expanded', 'false');
        bell.classList.remove('notif-bell-open');
    }

    toggle.addEventListener('click', function (ev) {
        ev.preventDefault();
        if (panel.hidden) openPanel();
        else closePanel();
    });

    // Click an item: fire-and-forget read marker, then let the anchor
    // navigate. keepalive=true so the POST survives the page-unload.
    list.addEventListener('click', function (ev) {
        var anchor = ev.target.closest('[data-notif-id]');
        if (!anchor) return;
        var id = anchor.getAttribute('data-notif-id');
        if (!id) return;
        try {
            fetch('/api/v1/notifications/' + encodeURIComponent(id) + '/read', {
                method: 'POST',
                credentials: 'same-origin',
                keepalive: true,
                skipLoader: true,
            });
        } catch (_) { /* ignore — server-side dedup means a missed mark is harmless */ }
        // Optimistically zero-out the unread state in the badge so the
        // count drops by 1 before the navigation completes.
        var currentBadge = Number(badge.textContent) || 0;
        if (currentBadge > 0) setBadge(currentBadge - 1);
    });

    if (markAllBtn) {
        markAllBtn.addEventListener('click', async function () {
            try {
                var res = await fetch('/api/v1/notifications/read-all', {
                    method: 'POST',
                    credentials: 'same-origin',
                    skipLoader: true,
                });
                if (!res.ok) return;
            } catch (_) { return; }
            setBadge(0);
            // Drop the "unread" class on every visible row.
            list.querySelectorAll('.notif-item-unread').forEach(function (el) {
                el.classList.remove('notif-item-unread');
            });
        });
    }

    // Click-outside dismissal — same shape as the search dropdown. The panel
    // is portaled to <body> (see init), so it's no longer inside the bell;
    // clicks within it must be treated as inside or every panel interaction
    // (Mark all read, item taps) would self-dismiss.
    document.addEventListener('click', function (ev) {
        if (panel.hidden) return;
        if (bell.contains(ev.target) || panel.contains(ev.target)) return;
        closePanel();
    });
    document.addEventListener('keydown', function (ev) {
        if (ev.key === 'Escape' && !panel.hidden) closePanel();
    });

    // Keep the panel anchored to the bell if the viewport changes while
    // it's open (e.g. window resize, devtools open, orientation change).
    window.addEventListener('resize', function () {
        if (!panel.hidden) positionPanel();
    });

    document.addEventListener('visibilitychange', function () {
        // Coming back to a foregrounded tab: kick off an immediate refresh
        // so the badge catches up after time-skipped scheduled timers.
        // (setTimeout is throttled or paused on hidden tabs in many
        // browsers, so the scheduled wake may have been missed.)
        if (!document.hidden) refreshCount();
    });
    refreshCount();
})();
