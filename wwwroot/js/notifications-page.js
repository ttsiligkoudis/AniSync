// /notifications page JS.
//
// Three things:
//   1. Infinite-scroll — IntersectionObserver on a sentinel; fetches
//      successive HTML chunks from /notifications/page?skip=N and
//      appends them. Same pattern as discover-pagination.js.
//   2. Selection — checkboxes per row + a Set of selected ids. Row
//      click navigates (the row is an <a>); checkbox / delete-icon
//      clicks stop propagation so they don't navigate, and instead
//      toggle selection / fire delete.
//   3. Bulk actions — when 1+ rows are selected, the page-header
//      toolbar swaps from "Mark all read" to "Mark read / Delete /
//      Cancel" against the selection.
(function () {
    'use strict';

    var paginator = document.querySelector('[data-notif-page-paginator]');
    if (!paginator) return;

    var sentinel = paginator.querySelector('.notif-page-sentinel');
    var loader = paginator.querySelector('.notif-page-loader');
    var skip = parseInt(paginator.getAttribute('data-skip') || '0', 10);
    var pageSize = parseInt(paginator.getAttribute('data-page-size') || '20', 10);
    var loading = false;
    var done = !sentinel;
    var observer = null;

    var toolbar = document.querySelector('[data-notif-page-toolbar]');
    var subtitle = document.querySelector('[data-notif-page-subtitle]');
    var btnMarkAll = toolbar && toolbar.querySelector('[data-notif-mark-all-read]');
    var btnBulkRead = toolbar && toolbar.querySelector('[data-notif-bulk-read]');
    var btnBulkDelete = toolbar && toolbar.querySelector('[data-notif-bulk-delete]');
    var btnBulkCancel = toolbar && toolbar.querySelector('[data-notif-bulk-cancel]');

    // Track the original "N notifications" subtitle so the selection
    // toggle can swap to "N selected" and back without server data.
    var defaultSubtitle = subtitle ? subtitle.textContent : '';

    var selected = new Set();

    function setBadgeOnBell(delta) {
        // Best-effort: nudge the layout-bell badge so it agrees with
        // what the user just did on this page without waiting for the
        // next scheduled refresh. The bell JS owns the badge element
        // and will overwrite this on its next /count fetch anyway.
        var bellBadge = document.querySelector('[data-notif-count]');
        if (!bellBadge) return;
        var current = Number((bellBadge.textContent || '').replace('+', '')) || 0;
        var next = Math.max(0, current + delta);
        bellBadge.textContent = next > 99 ? '99+' : String(next);
        bellBadge.hidden = next === 0;
    }

    function renderSelectionState() {
        var count = selected.size;
        var hasSel = count > 0;
        if (subtitle) {
            subtitle.textContent = hasSel
                ? (count + ' selected')
                : defaultSubtitle;
        }
        if (btnMarkAll) btnMarkAll.hidden = hasSel;
        if (btnBulkRead) btnBulkRead.hidden = !hasSel;
        if (btnBulkDelete) btnBulkDelete.hidden = !hasSel;
        if (btnBulkCancel) btnBulkCancel.hidden = !hasSel;
    }

    function setRowSelected(row, isSelected) {
        var id = row.getAttribute('data-notif-id');
        if (!id) return;
        if (isSelected) {
            selected.add(id);
            row.classList.add('notif-page-item-selected');
        } else {
            selected.delete(id);
            row.classList.remove('notif-page-item-selected');
        }
    }

    function wireRow(row) {
        var box = row.querySelector('.notif-page-checkbox-input');
        var checkboxWrap = row.querySelector('[data-notif-page-checkbox]');
        var deleteBtn = row.querySelector('[data-notif-page-delete]');

        if (box) {
            // Stop propagation so toggling the checkbox doesn't navigate
            // the <a> wrapper. Sync the row's selected class on every
            // change so styling tracks the box state.
            box.addEventListener('click', function (e) { e.stopPropagation(); });
            box.addEventListener('change', function () {
                setRowSelected(row, box.checked);
                renderSelectionState();
            });
        }
        if (checkboxWrap) {
            // Clicks anywhere on the wrap (incl. the padding around the
            // box) act on the box and don't navigate. Without this the
            // wrap's padding leaks through to the row's <a> click.
            checkboxWrap.addEventListener('click', function (e) {
                if (e.target === box) return; // browser handles it
                e.stopPropagation();
                e.preventDefault();
                if (box) {
                    box.checked = !box.checked;
                    box.dispatchEvent(new Event('change', { bubbles: true }));
                }
            });
        }

        if (deleteBtn) {
            deleteBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                e.preventDefault();
                var id = row.getAttribute('data-notif-id');
                if (!id) return;
                deleteBtn.disabled = true;
                fetch('/api/v1/notifications/' + encodeURIComponent(id), {
                    method: 'DELETE',
                    credentials: 'same-origin',
                    skipLoader: true,
                })
                    .then(function (r) {
                        if (!r.ok && r.status !== 404) {
                            deleteBtn.disabled = false;
                            return;
                        }
                        // Drop the row from the DOM + selection. Adjust
                        // the bell badge if the row was unread so the
                        // count tracks immediately.
                        var wasUnread = row.classList.contains('notif-page-item-unread');
                        setRowSelected(row, false);
                        row.remove();
                        if (wasUnread) setBadgeOnBell(-1);
                        renderSelectionState();
                    })
                    .catch(function () { deleteBtn.disabled = false; });
            });
        }

        // Mark-as-read fire-and-forget on row navigation, same shape as
        // the bell dropdown. keepalive lets the POST survive the page
        // unload that's about to happen.
        row.addEventListener('click', function () {
            var id = row.getAttribute('data-notif-id');
            if (!id) return;
            var wasUnread = row.classList.contains('notif-page-item-unread');
            if (!wasUnread) return;
            try {
                fetch('/api/v1/notifications/' + encodeURIComponent(id) + '/read', {
                    method: 'POST',
                    credentials: 'same-origin',
                    keepalive: true,
                    skipLoader: true,
                });
            } catch (_) { /* ignore */ }
            setBadgeOnBell(-1);
        });
    }

    function wireRowsIn(scope) {
        var rows = scope.querySelectorAll('.notif-page-item');
        for (var i = 0; i < rows.length; i++) wireRow(rows[i]);
    }

    // Wire the rows the server already rendered.
    wireRowsIn(paginator);

    // Toolbar handlers — Mark all read, bulk read, bulk delete, cancel.
    if (btnMarkAll) {
        btnMarkAll.addEventListener('click', function () {
            btnMarkAll.disabled = true;
            fetch('/api/v1/notifications/read-all', {
                method: 'POST',
                credentials: 'same-origin',
                skipLoader: true,
            })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) {
                    if (!data) return;
                    var unreadRows = paginator.querySelectorAll('.notif-page-item-unread');
                    var n = unreadRows.length;
                    unreadRows.forEach(function (r) { r.classList.remove('notif-page-item-unread'); });
                    if (n > 0) setBadgeOnBell(-n);
                })
                .finally(function () { btnMarkAll.disabled = false; });
        });
    }

    function bulkAction(endpoint, removeRows) {
        if (selected.size === 0) return;
        var ids = Array.from(selected).map(function (s) { return Number(s); }).filter(function (n) { return !isNaN(n); });
        if (ids.length === 0) return;

        // Snapshot the rows up front so we can update their state /
        // remove them in the success branch without re-querying.
        var rows = [];
        selected.forEach(function (id) {
            var row = paginator.querySelector('.notif-page-item[data-notif-id="' + id + '"]');
            if (row) rows.push(row);
        });

        if (btnBulkRead) btnBulkRead.disabled = true;
        if (btnBulkDelete) btnBulkDelete.disabled = true;

        fetch(endpoint, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ids: ids }),
            skipLoader: true,
        })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                var unreadCount = 0;
                rows.forEach(function (row) {
                    if (row.classList.contains('notif-page-item-unread')) unreadCount++;
                    if (removeRows) {
                        row.remove();
                    } else {
                        row.classList.remove('notif-page-item-unread');
                    }
                });
                // Bell counter mirrors whichever rows were unread.
                if (unreadCount > 0) setBadgeOnBell(-unreadCount);
                selected.clear();
                renderSelectionState();
            })
            .finally(function () {
                if (btnBulkRead) btnBulkRead.disabled = false;
                if (btnBulkDelete) btnBulkDelete.disabled = false;
            });
    }

    if (btnBulkRead) {
        btnBulkRead.addEventListener('click', function () {
            bulkAction('/api/v1/notifications/bulk-read', /* removeRows */ false);
        });
    }
    if (btnBulkDelete) {
        btnBulkDelete.addEventListener('click', function () {
            bulkAction('/api/v1/notifications/bulk-delete', /* removeRows */ true);
        });
    }
    if (btnBulkCancel) {
        btnBulkCancel.addEventListener('click', function () {
            paginator.querySelectorAll('.notif-page-item-selected').forEach(function (row) {
                var box = row.querySelector('.notif-page-checkbox-input');
                if (box) box.checked = false;
                row.classList.remove('notif-page-item-selected');
            });
            selected.clear();
            renderSelectionState();
        });
    }

    renderSelectionState();

    // ── Infinite scroll ────────────────────────────────────────────

    function teardown() {
        done = true;
        if (observer) { observer.disconnect(); observer = null; }
        if (sentinel && sentinel.parentNode) sentinel.parentNode.removeChild(sentinel);
        if (loader && loader.parentNode) loader.parentNode.removeChild(loader);
    }

    function appendChunk(html) {
        var temp = document.createElement('div');
        temp.innerHTML = html;
        var children = Array.prototype.slice.call(temp.querySelectorAll('.notif-page-item'));
        if (children.length === 0) return 0;
        // Insert before the sentinel so subsequent observer triggers
        // continue to fire on the same node as we keep appending.
        children.forEach(function (row) {
            if (sentinel && sentinel.parentNode) {
                sentinel.parentNode.insertBefore(row, sentinel);
            } else {
                paginator.appendChild(row);
            }
            wireRow(row);
        });
        return children.length;
    }

    function loadMore() {
        if (loading || done) return;
        loading = true;
        if (loader) loader.hidden = false;
        fetch('/notifications/page?skip=' + skip, {
            credentials: 'same-origin',
            headers: { 'Accept': 'text/html' },
            skipLoader: true,
        })
            .then(function (r) { return r.ok ? r.text() : null; })
            .then(function (html) {
                if (html === null || html === undefined) { teardown(); return; }
                var added = appendChunk(html);
                if (added === 0 || added < pageSize) {
                    // Either a stale upstream or we've reached the end.
                    // Either way the next sentinel hit wouldn't add anything.
                    skip += added;
                    teardown();
                } else {
                    skip += added;
                }
            })
            .catch(function () { /* keep sentinel — user can retry by scrolling */ })
            .finally(function () {
                loading = false;
                if (loader) loader.hidden = true;
            });
    }

    if (sentinel) {
        observer = new IntersectionObserver(function (entries) {
            for (var i = 0; i < entries.length; i++) {
                if (entries[i].isIntersecting) { loadMore(); return; }
            }
        }, { rootMargin: '400px' });
        observer.observe(sentinel);
    }
})();
