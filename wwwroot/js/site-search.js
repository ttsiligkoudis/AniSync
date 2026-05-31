// Global anime search dropdown for the layout's site-header.
//
// Drives every .site-search-input on the page (the layout renders one but
// future surfaces could opt in by adding the same DOM shape: a form
// .site-search wrapping the input and a sibling .site-search-results
// listbox). Debounces typing by ~250ms; only fires once the query is at
// least MIN_CHARS long so the upstream /api/v1/match endpoint isn't
// hammered for one-or-two-letter typos.
//
// AbortController cancels in-flight requests when the user keeps typing,
// so a slow 3-letter query never paints over a fast 5-letter follow-up.
(function () {
    'use strict';

    var DEBOUNCE_MS = 250;
    var MIN_CHARS = 3;
    var RESULT_LIMIT = 8;

    var forms = document.querySelectorAll('form.site-search');
    if (!forms.length) return;

    forms.forEach(setupForm);

    function setupForm(form) {
        var input = form.querySelector('.site-search-input');
        var results = form.querySelector('.site-search-results');
        if (!input || !results) return;

        var debounceTimer = null;
        var currentRequest = null;

        function hideResults() {
            results.hidden = true;
            results.innerHTML = '';
            input.setAttribute('aria-expanded', 'false');
        }

        function showResults() {
            results.hidden = false;
            input.setAttribute('aria-expanded', 'true');
        }

        function renderStatus(message) {
            results.innerHTML = '<div class="site-search-status">' + escapeHtml(message) + '</div>';
            showResults();
        }

        function renderMatches(matches) {
            if (!matches || matches.length === 0) {
                renderStatus('No matches.');
                return;
            }

            // Build each result row manually. Each anchor links to /meta/{id};
            // AnimeController.Detail resolves cross-service ids on click, so
            // we can hand back whatever id the /api/v1/match scored highest
            // for the user's primary service.
            var html = matches.map(function (m) {
                var name = m && m.name ? String(m.name) : '(no title)';
                var id = m && m.id ? String(m.id) : '';
                var poster = m && m.poster ? String(m.poster) : '';
                var type = m && m.type ? String(m.type).toLowerCase() : '';

                var posterHtml = poster
                    ? '<img class="site-search-result-poster" src="' + escapeAttr(poster) + '" alt="" loading="lazy" />'
                    : '<div class="site-search-result-poster site-search-result-poster-placeholder" aria-hidden="true"></div>';

                // Movie / TV badge — small visual differentiator so the
                // user can pick "Naruto Shippuden the Movie" vs the
                // series without parsing the title. Anything that isn't
                // explicitly "movie" reads as TV (the canonical anime
                // catalog default; covers both the "series" and legacy
                // "anime" type values upstream emits).
                var typeBadge = '';
                if (type === 'movie') {
                    typeBadge = '<span class="site-search-result-type site-search-result-type-movie">Movie</span>';
                } else if (type) {
                    typeBadge = '<span class="site-search-result-type site-search-result-type-tv">TV</span>';
                }

                return '<a class="site-search-result" role="option" href="/meta/' + encodeURIComponent(id) + '">'
                    + posterHtml
                    + '<span class="site-search-result-name">' + escapeHtml(name) + '</span>'
                    + typeBadge
                    + '</a>';
            }).join('');

            results.innerHTML = html;
            showResults();
        }

        function run(query) {
            // Cancel any in-flight request so the older response can't
            // overwrite a fresher one on slow networks.
            if (currentRequest) {
                try { currentRequest.abort(); } catch (e) { /* ignore */ }
            }
            currentRequest = new AbortController();
            var signal = currentRequest.signal;

            renderStatus('Searching…');

            fetch('/api/v1/match?title=' + encodeURIComponent(query) + '&limit=' + RESULT_LIMIT, {
                signal: signal,
                credentials: 'same-origin',
                headers: { 'Accept': 'application/json' },
                // Bypass the global loader overlay — typeahead fires a request
                // on nearly every keystroke after the 3-char threshold; the
                // full-viewport spinner would flash on every one of them.
                // The inline "Searching…" status inside the dropdown is the
                // honest indicator here.
                skipLoader: true
            })
                .then(function (r) {
                    if (!r.ok) throw new Error('http ' + r.status);
                    return r.json();
                })
                .then(function (data) {
                    if (signal.aborted) return;
                    renderMatches(data && data.matches);
                })
                .catch(function (err) {
                    if (err && err.name === 'AbortError') return;
                    renderStatus('Search unavailable.');
                });
        }

        input.addEventListener('input', function () {
            clearTimeout(debounceTimer);
            var query = input.value.trim();
            if (query.length < MIN_CHARS) {
                hideResults();
                return;
            }
            debounceTimer = setTimeout(function () { run(query); }, DEBOUNCE_MS);
        });

        // Refocus reopens the dropdown if we already have rendered results
        // for the current query — otherwise the user types one letter, clicks
        // away, clicks back, and has to start over.
        input.addEventListener('focus', function () {
            if (input.value.trim().length >= MIN_CHARS && results.innerHTML) {
                showResults();
            }
        });

        input.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                input.value = '';
                hideResults();
                input.blur();
            }
        });

        // Click outside the form closes the dropdown. Touch-friendly variant
        // bound to `mousedown` so the dismissal happens before any focus loss
        // animation kicks in.
        document.addEventListener('mousedown', function (e) {
            if (!form.contains(e.target)) hideResults();
        });

        // Enter submits the form; we wire that to "navigate to the top
        // result" so a hot user can just hit Return after typing instead
        // of grabbing the mouse.
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            var first = results.querySelector('.site-search-result');
            if (first) window.location.href = first.getAttribute('href');
        });
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c];
        });
    }

    function escapeAttr(s) {
        return escapeHtml(s);
    }
})();
