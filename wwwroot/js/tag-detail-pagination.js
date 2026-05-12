// Infinite-scroll for /discover/tag/{tagStr} (anime carrying a single tag).
//
// Same shape as studio-detail-pagination.js: page-based against AniList's
// Page connection, end-of-list driven by the X-Has-Next-Page header,
// post-load sentinel re-check to defeat IntersectionObserver's
// transitions-only firing on wide screens with short grids. Page.media
// already filters server-side by type:ANIME so we don't need the
// roll-forward-through-empty-pages dance the studio paginators do.
(function () {
    'use strict';

    var paginator = document.querySelector('.tag-detail-paginator');
    if (!paginator) return;

    var grid = paginator.querySelector('.library-grid');
    var sentinel = paginator.querySelector('.tag-detail-sentinel');
    var loader = paginator.querySelector('.paginator-loader');
    if (!grid || !sentinel) return;

    var tag = paginator.getAttribute('data-tag');
    var page = parseInt(paginator.getAttribute('data-page') || '1', 10);
    if (!tag) return;

    var loading = false;
    var done = false;
    var observer = null;

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
        if (sentinel && sentinel.parentNode) sentinel.parentNode.removeChild(sentinel);
        if (loader && loader.parentNode) loader.parentNode.removeChild(loader);
    }

    function showLoader() { if (loader) loader.hidden = false; }
    function hideLoader() { if (loader) loader.hidden = true; }

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

    function loadMore() {
        if (loading || done) return;
        loading = true;
        showLoader();

        var nextPage = page + 1;
        fetch('/discover/tag/' + encodeURIComponent(tag) + '/page?page=' + nextPage, {
            credentials: 'same-origin',
            headers: { 'Accept': 'text/html' },
            skipLoader: true
        })
            .then(function (r) {
                if (!r || !r.ok) { teardown(); return; }
                var hasNext = (r.headers.get('X-Has-Next-Page') || '').toLowerCase() === 'true';
                return r.text().then(function (html) {
                    var added = appendCards(html || '');
                    if (added > 0) page = nextPage;
                    if (!hasNext) teardown();
                });
            })
            .catch(function () { /* swallow — sentinel stays, retry on next scroll */ })
            .then(function () {
                loading = false;
                hideLoader();
                if (!done) requestAnimationFrame(kickIfStillVisible);
            });
    }

    function kickIfStillVisible() {
        if (done || loading || !sentinel) return;
        var rect = sentinel.getBoundingClientRect();
        if (rect.top < window.innerHeight + 400) {
            loadMore();
        }
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
