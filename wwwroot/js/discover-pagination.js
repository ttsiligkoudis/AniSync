// Infinite-scroll for /discover.
//
// Watches a sentinel <div> at the bottom of the grid; whenever it scrolls
// within rootMargin of the viewport, fetch the next chunk of cards from
// /discover/page and append them to the existing .library-grid. Bails
// when the server returns an empty payload (end of catalog) or any other
// failure mode.
//
// Page-size lives server-side (50 for AniList/MAL, 20 for Kitsu via
// CatalogPageSize). The client just tracks the running count of cards
// already rendered ("skip") and increments it by however many the next
// fetch returned — service-agnostic, no client-side knowledge of the
// per-service page size.
(function () {
    'use strict';

    var paginator = document.querySelector('.discover-paginator');
    if (!paginator) return;

    var grid = paginator.querySelector('.library-grid');
    var sentinel = paginator.querySelector('.discover-sentinel');
    var loader = paginator.querySelector('.paginator-loader');
    if (!grid || !sentinel) return;

    var list = paginator.getAttribute('data-list');
    var genre = paginator.getAttribute('data-genre') || '';
    var season = paginator.getAttribute('data-season') || '';
    var skip = parseInt(paginator.getAttribute('data-skip') || '0', 10);
    if (!list) return;

    var loading = false;
    var done = false;
    var observer = null;

    // Track the ids we've already rendered so a broken upstream pagination
    // (Kitsu's /anime?filter[season] endpoint silently ignores page[offset],
    // for example — every "next" page returns the same 20 entries) can't
    // duplicate cards in the grid. Seeded from the server-rendered initial
    // page; updated as we append. The ?? || '' guards against cards that
    // happen to lack data-meta-id (none today, but defensive).
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

    function showLoader() { if (loader) loader.hidden = false; }
    function hideLoader() { if (loader) loader.hidden = true; }

    function appendCards(html) {
        // The /discover/page partial renders <div class="library-grid">…
        // cards…</div>. Strip the wrapper and move each child <a> into the
        // existing grid so the layout (CSS grid columns + gap) stays
        // unified across appended chunks.
        //
        // Dedupe against seenIds before appending — protects against any
        // service path that doesn't honour our skip parameter (most
        // notably Kitsu's seasonal endpoint). Returns the count of cards
        // actually inserted, so the outer loadMore() can detect a
        // "nothing new" page and stop the observer.
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
            // Else: dropped on the floor (either a duplicate or a card
            // without an id). The original element either stays in the
            // detached `temp` wrapper or, if appended, is gone — either
            // way no further work needed.
            child = next;
        }
        return added;
    }

    function loadMore() {
        if (loading || done) return;
        loading = true;
        showLoader();

        var params = 'list=' + encodeURIComponent(list)
            + '&skip=' + skip;
        if (genre) params += '&genre=' + encodeURIComponent(genre);
        if (season) params += '&season=' + encodeURIComponent(season);

        // skipLoader: true bypasses the global full-screen loader-overlay —
        // we render the inline paginator-loader (above) instead so scrolling
        // gets a calm in-flow cue rather than the page-wide scrim.
        fetch('/discover/page?' + params, {
            credentials: 'same-origin',
            headers: { 'Accept': 'text/html' },
            skipLoader: true
        })
            .then(function (r) { return r.ok ? r.text() : null; })
            .then(function (html) {
                if (html === null || html === undefined) { teardown(); return; }
                var added = appendCards(html);
                if (added === 0) {
                    // Upstream returned an empty page → we've hit the end
                    // of the catalog (or the service rejected the request).
                    // Drop the sentinel so the observer can't fire again.
                    teardown();
                } else {
                    skip += added;
                }
            })
            .catch(function () { /* swallow — sentinel stays, user can scroll back up and retry by re-scrolling */ })
            .finally(function () { loading = false; hideLoader(); });
    }

    // rootMargin: 400px → load the next page before the sentinel enters
    // the viewport so the user rarely sees a stall. Trades a slightly
    // earlier upstream call for smoother scroll feel.
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
