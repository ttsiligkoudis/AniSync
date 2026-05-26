// Infinite-scroll for /discover.
//
// Watches a sentinel <div> at the bottom of the grid; whenever it scrolls
// within rootMargin of the viewport, fetch the next chunk of cards from
// /discover/page and append them to the existing .library-grid. Bails
// when the server returns an empty payload (end of catalog) or any other
// failure mode.
//
// 1-indexed page numbers on the wire. The server-rendered initial view
// is page 1 (data-page="1"); each loadMore() increments and sends the
// next page number. Previous version sent item-count skip, then the
// server divided by per-service CatalogPageSize to recover the page —
// that worked only when every page came back full. A seasonal listing
// that returned 30 cards on page 1 made the JS send skip=30, the server
// computed (30 / 50) + 1 = page 1 again, the JS deduped every card as
// already-seen and tore down the observer.
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
    var tag = paginator.getAttribute('data-tag') || '';
    // Last page already rendered. JS bumps this before each fetch so
    // the next request asks for page+1. The view emits page=0 when
    // the grid only holds skeleton placeholders — first loadMore()
    // call then asks for page=1 and replaces them with real cards.
    var page = parseInt(paginator.getAttribute('data-page') || '1', 10);
    if (!list) return;

    // True when the grid is currently rendered as skeleton placeholders
    // and the first loadMore() needs to wipe them before appending the
    // real cards. Set by the view via data-needs-initial-load when the
    // server skipped the upstream catalog fetch (the default for every
    // browse view — Trending / Seasonal / Airing / Tag — so the initial
    // paint isn't blocked behind AniList).
    var needsInitialLoad = paginator.getAttribute('data-needs-initial-load') === 'true';

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

        var nextPage = page + 1;
        var params = 'list=' + encodeURIComponent(list)
            + '&page=' + nextPage;
        if (genre) params += '&genre=' + encodeURIComponent(genre);
        if (season) params += '&season=' + encodeURIComponent(season);
        if (tag) params += '&tag=' + encodeURIComponent(tag);

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
                if (html === null || html === undefined) {
                    // Fetch failed. If we were still showing skeletons,
                    // wipe them so the user sees an honest empty state
                    // rather than a forever-shimmering grid.
                    if (needsInitialLoad) {
                        grid.replaceChildren();
                        needsInitialLoad = false;
                    }
                    teardown();
                    return;
                }
                // Replace the skeleton placeholders with the real cards
                // on the first successful fetch. seenIds is already
                // empty (skeletons carry no data-meta-id) so we don't
                // even need to reset it — just clear the grid before
                // appendCards runs.
                if (needsInitialLoad) {
                    grid.replaceChildren();
                    needsInitialLoad = false;
                }
                var added = appendCards(html);
                if (added === 0) {
                    // Upstream returned an empty page → we've hit the end
                    // of the catalog (or the service rejected the request).
                    // Drop the sentinel so the observer can't fire again.
                    teardown();
                } else {
                    page = nextPage;
                }
            })
            .catch(function () {
                // Same skeleton-wipe on network errors so the page
                // doesn't strand the user looking at placeholders.
                if (needsInitialLoad) {
                    grid.replaceChildren();
                    needsInitialLoad = false;
                }
            })
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

    // Kick the first fetch right away when the server skipped the
    // initial catalog query — discover-pagination.js owns page 1 in
    // that mode. The IntersectionObserver above may also fire on its
    // own if the sentinel was already in the viewport (short page
    // before cards arrive), but the `loading` guard inside loadMore()
    // collapses any race into a single fetch.
    if (needsInitialLoad) {
        loadMore();
    }
})();
