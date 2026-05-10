// Inline Manage Entry modal — used by the web-app pages (Library / Discover /
// Dashboard) to edit a list entry without navigating away to the standalone
// Manage Entry page. The page-version still exists as fallback for non-JS
// browsers and as the deep target of the cards' href attribute, so a JS
// failure here just means the user falls back to the page experience.
//
// Flow:
//   1. User clicks a poster card → JS intercepts (preventDefault) → modal opens.
//   2. Fetch entry via /api/library/entry → populate form. If the anime is a
//      franchise split across multiple service entries (Attack on Titan, JoJo,
//      etc.), the response includes a seasons array; the modal renders a
//      Season dropdown that re-fetches form data when changed.
//   3. User edits → submit → POST /api/library/entry/save (using the resolved
//      per-cour entry id, NOT the original card id).
//   4. Reload the page on success so the list reflects status changes (e.g.
//      moving from Watching → Completed). A "Saved" toast survives the reload
//      via sessionStorage.
(function () {
    'use strict';

    var modal = document.querySelector('.entry-modal');
    var backdrop = document.querySelector('.entry-modal-backdrop');
    if (!modal || !backdrop) return; // anonymous user — no modal in DOM

    var titleEl = modal.querySelector('.entry-modal-title');
    var loadingEl = modal.querySelector('.entry-modal-loading');
    var formEl = modal.querySelector('.entry-modal-form');
    var errorEl = modal.querySelector('.entry-modal-error');
    var seasonField = modal.querySelector('[data-season-field]');
    var seasonSelect = modal.querySelector('#entry-modal-season');
    var statusSelect = modal.querySelector('#entry-modal-status');
    var progressInput = modal.querySelector('#entry-modal-progress');
    var totalEl = modal.querySelector('.entry-modal-total');
    var scoreInput = modal.querySelector('#entry-modal-score');
    var startedInput = modal.querySelector('#entry-modal-started');
    var finishedInput = modal.querySelector('#entry-modal-finished');
    var rewatchInput = modal.querySelector('#entry-modal-rewatch');
    var notesInput = modal.querySelector('#entry-modal-notes');
    var cancelBtn = modal.querySelector('.entry-modal-cancel');
    var closeBtn = modal.querySelector('.entry-modal-close');
    var saveBtn = formEl.querySelector('.entry-modal-save');

    // Status enum values per service. AnimeService enum: 0=Kitsu, 1=Anilist,
    // 2=MyAnimeList. Mirrors the dropdown in Views/Meta/ManageEntry.cshtml so
    // the same payloads are accepted by the SaveEntry endpoint downstream.
    // The empty value ("None") deletes the entry — same semantic as the page.
    var STATUS_OPTIONS = {
        // Kitsu
        0: [
            ['',           '— None (not in list) —'],
            ['current',    'Watching'],
            ['planned',    'Planning'],
            ['completed',  'Completed'],
            ['on_hold',    'On Hold'],
            ['dropped',    'Dropped'],
        ],
        // AniList
        1: [
            ['',           '— None (not in list) —'],
            ['CURRENT',    'Watching'],
            ['PLANNING',   'Planning'],
            ['COMPLETED',  'Completed'],
            ['PAUSED',     'Paused'],
            ['DROPPED',    'Dropped'],
            ['REPEATING',  'Rewatching'],
        ],
        // MyAnimeList
        2: [
            ['',                '— None (not in list) —'],
            ['watching',        'Watching'],
            ['plan_to_watch',   'Planning'],
            ['completed',       'Completed'],
            ['on_hold',         'On Hold'],
            ['dropped',         'Dropped'],
            ['rewatching',      'Rewatching'],
        ],
    };

    // The currently-active per-cour entry id (anilist:N / kitsu:N / mal:N).
    // Distinct from the card's original id, which might be a cross-service
    // imdb:/tmdb: id that resolves to one of multiple per-cour entries. Save
    // payloads send activeEntryId, not the card id.
    var activeEntryId = null;

    function openModal(id, name) {
        titleEl.textContent = name || 'Manage entry';
        loadingEl.hidden = false;
        formEl.hidden = true;
        errorEl.hidden = true;
        modal.hidden = false;
        backdrop.hidden = false;
        document.body.classList.add('modal-open');
        // Reset season picker; will be re-populated by loadEntry if applicable.
        seasonField.hidden = true;
        loadEntry(id, /* isInitial */ true);
    }

    function closeModal() {
        modal.hidden = true;
        backdrop.hidden = true;
        document.body.classList.remove('modal-open');
        activeEntryId = null;
        saveBtn.disabled = false;
    }

    function showError(msg) {
        loadingEl.hidden = true;
        errorEl.textContent = msg;
        errorEl.hidden = false;
        formEl.hidden = false;
    }

    function loadEntry(id, isInitial) {
        if (!isInitial) {
            // Show a soft loading state without re-hiding the whole form so
            // the user keeps context (which season they picked) while the
            // fields refresh.
            saveBtn.disabled = true;
        }
        return fetch('/api/library/entry?id=' + encodeURIComponent(id), {
            credentials: 'same-origin',
            headers: { 'Accept': 'application/json' },
        })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (!data || !data.success) {
                    showError('Failed to load entry. Try again.');
                    return;
                }
                // selectedEntryId is the per-cour id the server resolved this
                // request to; for native ids it's the same as the request id,
                // for cross-service ids it's the picked cour. Save targets
                // this id explicitly.
                activeEntryId = data.selectedEntryId || id;

                // Seasons dropdown: only render when the franchise has 2+
                // mappings. Single-mapping responses don't need a picker
                // (server still resolves to the per-cour id, just no choice
                // for the user to make).
                if (data.seasons && data.seasons.length > 1 && isInitial) {
                    seasonSelect.innerHTML = '';
                    data.seasons.forEach(function (s) {
                        var opt = document.createElement('option');
                        opt.value = s.id;
                        opt.textContent = s.label;
                        if (s.totalEpisodes != null) opt.setAttribute('data-total', s.totalEpisodes);
                        if (s.id === activeEntryId) opt.selected = true;
                        seasonSelect.appendChild(opt);
                    });
                    seasonField.hidden = false;
                } else if (isInitial) {
                    seasonField.hidden = true;
                }

                populateStatusOptions(data.service);
                statusSelect.value = data.status || '';
                progressInput.value = data.progress || 0;
                if (data.totalEpisodes && data.totalEpisodes > 0) {
                    progressInput.max = data.totalEpisodes;
                    totalEl.textContent = '/ ' + data.totalEpisodes;
                    totalEl.hidden = false;
                } else {
                    progressInput.removeAttribute('max');
                    totalEl.hidden = true;
                }
                scoreInput.value = data.score || '';
                startedInput.value = data.startedAt || '';
                finishedInput.value = data.finishedAt || '';
                rewatchInput.value = data.rewatchCount || 0;
                notesInput.value = data.notes || '';
                loadingEl.hidden = true;
                formEl.hidden = false;
                saveBtn.disabled = false;
            })
            .catch(function () {
                showError('Network error loading entry.');
                saveBtn.disabled = false;
            });
    }

    function populateStatusOptions(service) {
        var opts = STATUS_OPTIONS[service] || STATUS_OPTIONS[1];
        statusSelect.innerHTML = '';
        opts.forEach(function (pair) {
            var opt = document.createElement('option');
            opt.value = pair[0];
            opt.textContent = pair[1];
            statusSelect.appendChild(opt);
        });
    }

    // Card click interception. Targets only <a class="library-card"> elements
    // that carry a data-meta-id (the link variant; inert anonymous cards are
    // <div>s and don't match). Falls back to default navigation if the data
    // attribute is missing — keeps the JS as progressive enhancement.
    document.addEventListener('click', function (e) {
        var card = e.target.closest && e.target.closest('a.library-card[data-meta-id]');
        if (!card) return;
        e.preventDefault();
        openModal(card.getAttribute('data-meta-id'), card.getAttribute('data-meta-name'));
    });

    closeBtn.addEventListener('click', closeModal);
    cancelBtn.addEventListener('click', closeModal);
    backdrop.addEventListener('click', closeModal);

    // Season change — re-fetch entry data for the picked cour. The dropdown
    // stays visible (isInitial: false) so the user keeps their picker context;
    // only the form fields below refresh. activeEntryId updates inside
    // loadEntry, so the next save targets the picked cour.
    seasonSelect.addEventListener('change', function () {
        loadEntry(seasonSelect.value, /* isInitial */ false);
    });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && !modal.hidden) closeModal();
    });

    formEl.addEventListener('submit', function (e) {
        e.preventDefault();
        if (!activeEntryId) return;
        saveBtn.disabled = true;
        errorEl.hidden = true;

        var payload = {
            // Send the resolved per-cour entry id so the SaveEntry endpoint
            // doesn't have to redo the seasons resolution server-side. For
            // single-mapping anime this matches the original card id.
            id: activeEntryId,
            status: statusSelect.value,
            progress: parseInt(progressInput.value || '0', 10),
            score: scoreInput.value ? parseFloat(scoreInput.value) : null,
            // Date inputs serialise to "yyyy-MM-dd" or empty string when unset.
            // The server's ParseDate treats empty/invalid as null, so empty
            // strings here are equivalent to "no date set".
            startedAt: startedInput.value || null,
            finishedAt: finishedInput.value || null,
            rewatchCount: rewatchInput.value ? parseInt(rewatchInput.value, 10) : null,
            notes: notesInput.value || null,
        };

        fetch('/api/library/entry/save', {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'Content-Type': 'application/json',
                'Accept': 'application/json',
            },
            body: JSON.stringify(payload),
        })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data && data.success) {
                    // Queue a "Saved" toast that survives the page reload via
                    // sessionStorage — toast.js pops it on the next page load.
                    // Reload so list/grid reflects the change (status moves
                    // like Watching → Completed only show up after a refetch);
                    // v1 polish could swap to optimistic DOM update + skip
                    // reload, but the per-service status→tab mapping is fiddly
                    // and a full refresh is honest about server state.
                    try { sessionStorage.setItem('anisync-toast', 'Saved'); }
                    catch (e) { /* private-browsing or quota — proceed without toast */ }
                    window.location.reload();
                } else {
                    showError('Save failed. Try again.');
                    saveBtn.disabled = false;
                }
            })
            .catch(function () {
                showError('Network error saving.');
                saveBtn.disabled = false;
            });
    });
})();
