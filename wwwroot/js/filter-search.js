// Async submit for the /library and /discover filter bar.
//
// Without this, clicking the Search button would full-reload the page —
// expensive (re-renders layout chrome, re-runs layout JS) and visually
// jarring (scroll jumps, the tab strip flickers). With it, the form
// submit is intercepted, the relevant /library/page or /discover/page
// endpoint is hit with the same query string the GET would have used,
// and the response (just the poster-grid partial) is dropped into the
// results pane in place. URL is pushed to history so deep-linking and
// the browser back button still work.
//
// The two pages share this single script — selectors live as a per-page
// config object indexed by the form's action URL pathname. Whichever
// page the user is on, only its config matches.
(function () {
    'use strict';

    var form = document.querySelector('form.filter-bar');
    if (!form) return;

    // Per-page config: which pane to swap, which endpoint to call, and —
    // for pages that paginate (just /discover today; /library renders the
    // user's full list in one go) — which pagination script to re-load
    // after a swap so the infinite-scroll observer rebinds against the
    // freshly-rendered wrapper. /library's paginationScript / paginatorSelector
    // are absent because there is no pagination on that page.
    var configs = {
        '/library': {
            paneId: 'library-results-pane',
            pageEndpoint: '/library/page',
            indexPath: '/library',
        },
        '/discover': {
            paneId: 'discover-results-pane',
            pageEndpoint: '/discover/page',
            paginationScript: '/js/discover-pagination.js',
            paginatorSelector: '.discover-paginator',
            indexPath: '/discover',
        },
    };

    var actionPath = new URL(form.action, window.location.origin).pathname;
    var config = configs[actionPath];
    if (!config) return;

    var pane = document.getElementById(config.paneId);
    if (!pane) return;

    function buildQuery() {
        // FormData → URLSearchParams handles repeated keys + URI encoding for
        // free. We drop blank string values so an empty Search input doesn't
        // produce a ?search= that the server would have to ignore.
        var data = new FormData(form);
        var params = new URLSearchParams();
        data.forEach(function (value, key) {
            var trimmed = typeof value === 'string' ? value.trim() : value;
            if (trimmed === '' || trimmed == null) return;
            params.append(key, trimmed);
        });
        return params;
    }

    function reloadPaginationScript() {
        // The pagination script self-binds at script-load time against its
        // paginator wrapper. Re-inject only when the new pane actually
        // rendered one — the script's own early-return guards against
        // double-firing, but a re-add when there's nothing to bind to is
        // wasted work. Cache-bust the src so the script body re-runs.
        if (!config.paginationScript) return;
        if (!pane.querySelector(config.paginatorSelector)) return;
        var s = document.createElement('script');
        s.src = config.paginationScript + '?t=' + Date.now();
        document.body.appendChild(s);
    }

    // True when the pane was rendered with skeleton placeholders and the
    // server skipped the upstream fetch — runSubmit() needs to know
    // about this so the initial replace doesn't scroll the pane into
    // view (no real user action triggered it) and doesn't push a
    // history entry (the URL the user is already on is correct).
    function runSubmit(opts) {
        opts = opts || {};
        var params = buildQuery();
        // fullPane=1 tells /discover/page to render the "Empty results"
        // message for empty payloads (the pagination-scroll path keeps it
        // null since the JS just stops the observer). /library/page ignores
        // the flag — its single render path already includes the message.
        params.set('fullPane', '1');
        var url = config.pageEndpoint + '?' + params.toString();

        return fetch(url, {
            credentials: 'same-origin',
            headers: { 'Accept': 'text/html' },
        })
            .then(function (r) { return r.ok ? r.text() : null; })
            .then(function (html) {
                if (html === null) return;
                pane.innerHTML = html;
                pane.removeAttribute('data-needs-initial-load');
                reloadPaginationScript();

                if (!opts.initial) {
                    // Push the corresponding visible URL so deep-linking + back
                    // navigation works. Strip the fullPane=1 we appended for the
                    // partial endpoint — it has no meaning on the Index route.
                    params.delete('fullPane');
                    var visibleUrl = config.indexPath + (params.toString() ? ('?' + params.toString()) : '');
                    window.history.pushState({ filterSearch: true }, '', visibleUrl);
                }
                // Scroll position is left alone on both paths. Earlier we
                // smooth-scrolled the pane into view after a manual submit
                // so users who had scrolled far down would still see the
                // refreshed results, but the user reported the jump as
                // disruptive — the new results land in place, and the user
                // can scroll back up themselves if they want to.
            })
            .catch(function () {
                // On failure, fall back to a real navigation — that way the
                // user still gets results (or a real error page) instead of
                // staring at stale content. On the auto-initial-load path
                // a hard reload would loop infinitely (same URL, same skip-
                // upstream-fetch decision); leave the skeleton in place and
                // log to the console so the user can manually refresh.
                if (opts.initial) {
                    console.warn('filter-search: initial load failed; skeletons remain visible');
                    return;
                }
                window.location.href = form.action + '?' + buildQuery().toString();
            });
    }

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        runSubmit();
    });

    // Auto-fire the same submit pipeline on script load when the pane
    // is rendered as skeletons — the server deferred the upstream fetch
    // and this is what populates the page. The form's current state
    // (active tab, any genre / search the user navigated into via a
    // querystring) carries through buildQuery() unchanged, so a deep-
    // link like /library?list=completed lands on the right list.
    if (pane.getAttribute('data-needs-initial-load') === 'true') {
        runSubmit({ initial: true });
    }

    // Back / forward navigation: reload so the server-rendered state
    // matches the URL. Simpler than re-fetching + re-binding everything
    // from the JS side, and these navigations are rare compared to
    // forward Search clicks.
    window.addEventListener('popstate', function (e) {
        if (e.state && e.state.filterSearch) {
            window.location.reload();
        }
    });
})();
