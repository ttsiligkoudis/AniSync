// Infinite-scroll for /discover/studio/{id} (a studio's anime catalog).
//
// Shape mirrors studio-pagination.js (the /discover/studio listing):
//   - page-based, 1-indexed, matches AniList's own pagination
//   - end-of-list comes from the X-Has-Next-Page header, NOT from
//     "added === 0", because Studio.media returns Manga + Anime mixed
//     and the server filters manga client-side — a page can render zero
//     cards while real anime pages still follow
//   - after a successful render we re-check sentinel visibility because
//     IntersectionObserver only fires on transitions, and a wide grid
//     plus short poster row can leave the sentinel inside rootMargin
//     after the load (same gotcha as the studios listing).
(function () {
    'use strict';

    var paginator = document.querySelector('.studio-detail-paginator');
    if (!paginator) return;

    var grid = paginator.querySelector('.library-grid');
    var sentinel = paginator.querySelector('.studio-detail-sentinel');
    var loader = paginator.querySelector('.paginator-loader');
    if (!grid || !sentinel) return;

    var studioId = paginator.getAttribute('data-studio-id');
    var page = parseInt(paginator.getAttribute('data-page') || '1', 10);
    if (!studioId) return;

    var loading = false;
    var done = false;
    var observer = null;

    // Cap on consecutive empty-but-hasNext pages before we hand control
    // back to the user. Same guard as the studios listing: protects
    // against a runaway loop if AniList ever returns a long tail of
    // pure-manga pages or hasNextPage=true forever.
    var EMPTY_PAGE_CAP = 10;

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
        // The /discover/studio/{id}/page partial returns
        // <div class="library-grid">…cards…</div>. Strip the wrapper and
        // move each child into the existing grid so the CSS-grid layout
        // stays unified across appended chunks.
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

    function fetchUntilNonEmpty(startPage) {
        var attempts = 0;
        function step(p) {
            if (done) return Promise.resolve();
            attempts++;
            return fetch('/discover/studio/' + encodeURIComponent(studioId) + '/page?page=' + p, {
                credentials: 'same-origin',
                headers: { 'Accept': 'text/html' },
                skipLoader: true
            }).then(function (r) {
                if (!r || !r.ok) { teardown(); return; }
                var hasNext = (r.headers.get('X-Has-Next-Page') || '').toLowerCase() === 'true';
                return r.text().then(function (html) {
                    var added = appendCards(html || '');
                    if (added > 0) {
                        page = p;
                        if (!hasNext) teardown();
                        return;
                    }
                    if (!hasNext) { teardown(); return; }
                    if (attempts >= EMPTY_PAGE_CAP) {
                        page = p;
                        return;
                    }
                    return step(p + 1);
                });
            });
        }
        return step(startPage);
    }

    function loadMore() {
        if (loading || done) return;
        loading = true;
        showLoader();

        fetchUntilNonEmpty(page + 1)
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
