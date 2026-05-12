// Infinite-scroll for /studio.
//
// Page-based like AniList itself (1-indexed). End-of-list comes from the
// server's X-Has-Next-Page header, NOT from "added === 0", because the
// server filters out manga/LN labels and zero-anime studios — a given
// AniList page can legitimately yield zero tiles while still having
// real pages after it. So when a page renders nothing but the header
// says hasNext=true, we transparently fetch the next page in the same
// loadMore() pass so the user never sees the scroll stall.
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

    // Cap on consecutive empty-but-hasNext pages we'll roll through in one
    // loadMore call. Guards against a runaway loop if AniList ever returns
    // hasNextPage=true forever (or a long stretch of all-filtered pages
    // would spin the upstream API). 10 * 50 = 500 studios scanned — well
    // past any realistic "all manga labels" cluster in the NAME ordering.
    var EMPTY_PAGE_CAP = 10;

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

    // Walk forward through AniList pages until either (a) we render at
    // least one tile, (b) the server reports hasNext=false, or (c) we
    // hit the safety cap. Returns a promise so the outer loadMore can
    // hide the loader / re-arm cleanly.
    function fetchUntilNonEmpty(startPage) {
        var attempts = 0;
        function step(p) {
            if (done) return Promise.resolve();
            attempts++;
            return fetch('/studio/page?page=' + p, {
                credentials: 'same-origin',
                headers: { 'Accept': 'text/html' },
                skipLoader: true
            }).then(function (r) {
                if (!r || !r.ok) { teardown(); return; }
                var hasNext = (r.headers.get('X-Has-Next-Page') || '').toLowerCase() === 'true';
                return r.text().then(function (html) {
                    var added = appendTiles(html || '');
                    if (added > 0) {
                        page = p;
                        if (!hasNext) teardown();
                        return;
                    }
                    // Empty render. If upstream has more pages and we
                    // haven't blown the cap, roll forward without
                    // returning control — the user shouldn't have to
                    // scroll again to skip a barren cluster.
                    if (!hasNext) { teardown(); return; }
                    if (attempts >= EMPTY_PAGE_CAP) {
                        // Safety bail. Advance `page` so a subsequent
                        // sentinel hit picks up where we left off
                        // instead of redoing the same empty stretch.
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
                // IntersectionObserver only fires on transitions, not on
                // "still intersecting". On wide screens the 50-tile
                // grid is short enough that the sentinel can sit inside
                // the 400px rootMargin even after a successful load —
                // the observer's already-fired entry won't re-trigger
                // until the sentinel exits and re-enters the margin.
                // Re-check after layout settles and kick again so the
                // grid keeps filling without the user having to scroll.
                if (!done) {
                    requestAnimationFrame(kickIfStillVisible);
                }
            });
    }

    function kickIfStillVisible() {
        if (done || loading || !sentinel) return;
        var rect = sentinel.getBoundingClientRect();
        // Mirror the observer's rootMargin so the trigger threshold
        // stays consistent whether the load was kicked by a scroll
        // event or by this post-load self-check.
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
