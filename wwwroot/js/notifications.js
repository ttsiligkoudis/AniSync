// Bell + dropdown for per-user episode notifications in the site header.
//
// Polls /api/v1/notifications/count every 60s while the tab is visible to
// keep the unread badge fresh; pauses while the document is hidden so
// background tabs don't burn requests. Click of the bell expands the
// dropdown, which fetches the most recent 20 notifications and renders
// each as an <a> linking to the episode's watch page. Clicking a row
// fires a fire-and-forget POST /{id}/read (with keepalive so the marker
// survives navigation) and lets the anchor navigate.
(function () {
    'use strict';

    var POLL_MS = 60000;
    var COUNT_URL = '/api/v1/notifications/count';
    var LIST_URL = '/api/v1/notifications';

    var bell = document.querySelector('[data-notif-bell]');
    if (!bell) return;
    var toggle = bell.querySelector('[data-notif-toggle]');
    var panel = bell.querySelector('#notif-panel');
    var badge = bell.querySelector('[data-notif-count]');
    var list = bell.querySelector('[data-notif-list]');
    var emptyState = bell.querySelector('[data-notif-empty]');
    var markAllBtn = bell.querySelector('[data-notif-read-all]');
    if (!toggle || !panel || !badge || !list) return;

    var listLoaded = false;

    function setBadge(count) {
        var n = Number(count) || 0;
        badge.textContent = n > 99 ? '99+' : String(n);
        badge.hidden = n === 0;
    }

    async function pollCount() {
        if (document.hidden) return;
        try {
            var res = await fetch(COUNT_URL, { credentials: 'same-origin' });
            if (!res.ok) return;
            var data = await res.json();
            setBadge(data && data.count);
        } catch (_) { /* network blip — try again next tick */ }
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
            var res = await fetch(LIST_URL, { credentials: 'same-origin' });
            if (!res.ok) return;
            var data = await res.json();
            renderItems(data && data.items);
            listLoaded = true;
        } catch (_) { /* keep last state — user can retry by reopening */ }
    }

    function openPanel() {
        panel.hidden = false;
        toggle.setAttribute('aria-expanded', 'true');
        bell.classList.add('notif-bell-open');
        // Lazy-load on first open; subsequent opens reuse the rendered list
        // and rely on the 60s poll for new counts. The user can close and
        // reopen to force a refresh.
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

    // Click-outside dismissal — same shape as the search dropdown.
    document.addEventListener('click', function (ev) {
        if (panel.hidden) return;
        if (bell.contains(ev.target)) return;
        closePanel();
    });
    document.addEventListener('keydown', function (ev) {
        if (ev.key === 'Escape' && !panel.hidden) closePanel();
    });

    document.addEventListener('visibilitychange', function () {
        if (!document.hidden) pollCount();
    });
    setInterval(pollCount, POLL_MS);
    pollCount();
})();
