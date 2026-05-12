// Infinite-scroll for /studio.
//
// Same shape as discover-pagination.js but page-based instead of skip-based:
// AniList's Page.studios connection is 1-indexed and a NAME-sorted walk
// returns exactly perPage studios per page (modulo end-of-list), so the
// client just increments page and fetches /studio/page?page=N+1 when the
// sentinel scrolls into view. Empty response = end of catalog.
(function () {
    'use strict';

    var paginator = document.querySelector('.studio-paginator');
    if (!paginator) return;

    var grid = paginator.querySelector('.studio-grid');
    var sentinel = paginator.querySelector('.studio-sentinel');
    var loader = paginator.querySelector('.paginator-loader');
    if (!grid || !sentinel) return;

    var page = parseInt(paginator.getAttribute('data-page') || '1', 10);
    var loading = false;
    var done = false;
    var observer = null;

    // Dedupe by studio id so a momentary upstream hiccup that returns the
    // same AniList page twice can't render duplicate tiles. Seeded from
    // the server-rendered initial page.
    var seenIds = new Set();
    Array.prototype.forEach.call(grid.querySelectorAll('[data-studio-id]'), function (el) {
        var id = el.getAttribute('data-studio-id');
        if (id) seenIds.add(id);
    });

    function teardown() {
        done = true;
        if (observer) {
            observer.disconnect();
            observer = null;
        }
        if (sentinel && sentinel.parentNode) sentinel.parentNode.removeChild(sentinel);
        if (loader && loader.parentNode) loader.parentNode.removeChild(loader);
    }

    function showLoader() { if (loader) loader.hidden = false; }
    function hideLoader() { if (loader) loader.hidden = true; }

    function appendTiles(html) {
        // The /studio/page partial returns bare tile <a> elements (no
        // wrapping grid div, unlike _PosterGrid). Wrap in a temporary
        // container so we can iterate children and move them into the
        // live grid one at a time, with the dedupe guard.
        var temp = document.createElement('div');
        temp.innerHTML = html;
        var added = 0;
        var child = temp.firstElementChild;
        while (child) {
            var next = child.nextElementSibling;
            var id = child.getAttribute && child.getAttribute('data-studio-id');
            if (id && !seenIds.has(id)) {
                seenIds.add(id);
                grid.appendChild(child);
                added++;
            }
            child = next;
        }
        return added;
    }

    function loadMore() {
        if (loading || done) return;
        loading = true;
        showLoader();

        var nextPage = page + 1;
        fetch('/studio/page?page=' + nextPage, {
            credentials: 'same-origin',
            headers: { 'Accept': 'text/html' },
            skipLoader: true
        })
            .then(function (r) { return r.ok ? r.text() : null; })
            .then(function (html) {
                if (html === null || html === undefined) { teardown(); return; }
                var added = appendTiles(html);
                if (added === 0) {
                    teardown();
                } else {
                    page = nextPage;
                }
            })
            .catch(function () { /* swallow — sentinel stays, user can scroll back up and retry */ })
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
