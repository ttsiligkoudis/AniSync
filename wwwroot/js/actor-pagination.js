// Infinite-scroll for the /discover/actors directory (TMDB /person/popular).
// Page-based (1-indexed); end-of-list comes from the server's X-Has-Next-Page
// header. Unlike the studios paginator, TMDB pages are always dense, so there's
// no empty-but-hasNext walk to handle.
(function () {
    'use strict';

    var paginator = document.querySelector('.actor-paginator');
    if (!paginator) return;

    var grid = paginator.querySelector('.actor-grid');
    var sentinel = paginator.querySelector('.actor-sentinel');
    var loader = paginator.querySelector('.paginator-loader');
    if (!grid || !sentinel) return;

    var page = parseInt(paginator.getAttribute('data-page') || '1', 10);
    // Forwarded on each page fetch so search results paginate too (TMDB
    // /search/person), mirroring the studios paginator.
    var searchTerm = paginator.getAttribute('data-search') || '';
    var loading = false;
    var done = paginator.getAttribute('data-has-next') !== 'true';
    var observer = null;

    var seenIds = new Set();
    Array.prototype.forEach.call(grid.querySelectorAll('[data-actor-id]'), function (el) {
        var id = el.getAttribute('data-actor-id');
        if (id) seenIds.add(id);
    });

    function teardown() {
        done = true;
        if (observer) { observer.disconnect(); observer = null; }
        if (sentinel && sentinel.parentNode) sentinel.parentNode.removeChild(sentinel);
        if (loader && loader.parentNode) loader.parentNode.removeChild(loader);
    }

    function appendTiles(html) {
        var temp = document.createElement('div');
        temp.innerHTML = html;
        var added = 0;
        var child = temp.firstElementChild;
        while (child) {
            var next = child.nextElementSibling;
            var id = child.getAttribute && child.getAttribute('data-actor-id');
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
        if (loader) loader.hidden = false;

        var url = '/discover/actors/page?page=' + (page + 1)
            + (searchTerm ? '&search=' + encodeURIComponent(searchTerm) : '');
        fetch(url, {
            credentials: 'same-origin',
            headers: { 'Accept': 'text/html' },
            skipLoader: true
        }).then(function (r) {
            if (!r || !r.ok) { teardown(); return; }
            var hasNext = (r.headers.get('X-Has-Next-Page') || '').toLowerCase() === 'true';
            return r.text().then(function (html) {
                appendTiles(html || '');
                page++;
                if (!hasNext) teardown();
            });
        }).catch(function () {
            /* swallow — sentinel stays, retry on next scroll */
        }).then(function () {
            loading = false;
            if (loader) loader.hidden = true;
            if (!done) requestAnimationFrame(kickIfStillVisible);
        });
    }

    function kickIfStillVisible() {
        if (done || loading || !sentinel) return;
        var rect = sentinel.getBoundingClientRect();
        if (rect.top < window.innerHeight + 400) loadMore();
    }

    if (done) {
        if (sentinel && sentinel.parentNode) sentinel.parentNode.removeChild(sentinel);
        return;
    }

    observer = new IntersectionObserver(function (entries) {
        for (var i = 0; i < entries.length; i++) {
            if (entries[i].isIntersecting) { loadMore(); return; }
        }
    }, { rootMargin: '400px' });

    observer.observe(sentinel);
})();
