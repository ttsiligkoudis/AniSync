// Infinite-scroll for /library. Sister of discover-pagination.js with the
// same sentinel-watch pattern; differs only in the endpoint hit and which
// data-* attributes drive the query (list / search / genre instead of
// list / genre / season).
//
// Each scroll-trigger fetches /library/page?list=...&skip=N from the
// server. /library/page refetches the full list from the upstream
// service every time (we dropped the user-list cache by design — every
// load reflects live state) and slices the requested window. The cards
// returned are appended to the existing .library-grid; the running
// skip counter is incremented by however many cards actually landed.
(function () {
    'use strict';

    var paginator = document.querySelector('.library-paginator');
    if (!paginator) return;

    var grid = paginator.querySelector('.library-grid');
    var sentinel = paginator.querySelector('.library-sentinel');
    var loader = paginator.querySelector('.paginator-loader');
    if (!grid || !sentinel) return;

    var list = paginator.getAttribute('data-list');
    var search = paginator.getAttribute('data-search') || '';
    var genre = paginator.getAttribute('data-genre') || '';
    var skip = parseInt(paginator.getAttribute('data-skip') || '0', 10);
    if (!list) return;

    var loading = false;
    var done = false;
    var observer = null;

    // Dedup against ids already on the page so a service that ignores the
    // skip parameter (or returns the same window twice on a stutter)
    // doesn't produce duplicate cards. Seeded from the server-rendered
    // first page; updated as we append.
    var seenIds = new Set();
    Array.prototype.forEach.call(grid.querySelectorAll('[data-meta-id]'), function (el) {
        var id = el.getAttribute('data-meta-id');
        if (id) seenIds.add(id);
    });

    function teardown() {
        done = true;
        if (observer) {
            observer.disconnect();
            observer = null;
        }
        if (sentinel && sentinel.parentNode) {
            sentinel.parentNode.removeChild(sentinel);
        }
        if (loader && loader.parentNode) {
            loader.parentNode.removeChild(loader);
        }
    }

    function appendCards(html) {
        var temp = document.createElement('div');
        temp.innerHTML = html;
        var newGrid = temp.querySelector('.library-grid');
        if (!newGrid) return 0;
        var added = 0;
        var child = newGrid.firstElementChild;
        while (child) {
            var next = child.nextElementSibling;
            var id = child.getAttribute && child.getAttribute('data-meta-id');
            if (id && !seenIds.has(id)) {
                seenIds.add(id);
                grid.appendChild(child);
                added++;
            }
            child = next;
        }
        return added;
    }

    function showLoader() { if (loader) loader.hidden = false; }
    function hideLoader() { if (loader) loader.hidden = true; }

    function loadMore() {
        if (loading || done) return;
        loading = true;
        showLoader();

        var params = 'list=' + encodeURIComponent(list) + '&skip=' + skip;
        if (search) params += '&search=' + encodeURIComponent(search);
        if (genre) params += '&genre=' + encodeURIComponent(genre);

        // skipLoader: true bypasses the global full-screen loader-overlay —
        // we render the inline paginator-loader (above) instead so scrolling
        // gets a calm in-flow cue rather than the page-wide scrim.
        fetch('/library/page?' + params, {
            credentials: 'same-origin',
            headers: { 'Accept': 'text/html' },
            skipLoader: true,
        })
            .then(function (r) { return r.ok ? r.text() : null; })
            .then(function (html) {
                if (html === null || html === undefined) { teardown(); return; }
                var added = appendCards(html);
                if (added === 0) {
                    teardown();
                } else {
                    skip += added;
                }
            })
            .catch(function () { /* swallow — user can retry by scrolling */ })
            .finally(function () { loading = false; hideLoader(); });
    }

    observer = new IntersectionObserver(function (entries) {
        for (var i = 0; i < entries.length; i++) {
            if (entries[i].isIntersecting) {
                loadMore();
                return;
            }
        }
    }, { rootMargin: '400px' });

    observer.observe(sentinel);
})();
