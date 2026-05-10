// Inline Manage Entry modal — used by the web-app pages (Library / Discover /
// Dashboard) to edit a list entry without navigating away to the standalone
// Manage Entry page. The page-version still exists as fallback for non-JS
// browsers and as the deep target of the cards' href attribute, so a JS
// failure here just means the user falls back to the page experience.
//
// Flow:
//   1. User clicks a poster card → JS intercepts (preventDefault) → modal opens.
//   2. Fetch entry via /api/library/entry → populate form.
//   3. User edits → submit → POST /api/library/entry/save.
//   4. Reload the page on success so the list reflects status changes (e.g.
//      moving from Watching → Completed).
(function () {
    'use strict';

    var modal = document.querySelector('.entry-modal');
    var backdrop = document.querySelector('.entry-modal-backdrop');
    if (!modal || !backdrop) return; // anonymous user — no modal in DOM

    var titleEl = modal.querySelector('.entry-modal-title');
    var loadingEl = modal.querySelector('.entry-modal-loading');
    var formEl = modal.querySelector('.entry-modal-form');
    var errorEl = modal.querySelector('.entry-modal-error');
    var statusSelect = modal.querySelector('#entry-modal-status');
    var progressInput = modal.querySelector('#entry-modal-progress');
    var totalEl = modal.querySelector('.entry-modal-total');
    var scoreInput = modal.querySelector('#entry-modal-score');
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
            ['',                'â€” None (not in list) â€”'],
            ['watching',        'Watching'],
            ['plan_to_watch',   'Planning'],
            ['completed',       'Completed'],
            ['on_hold',         'On Hold'],
            ['dropped',         'Dropped'],
            ['rewatching',      'Rewatching'],
        ],
    };
    // Fix the mojibake from the literal above (the editor occasionally
    // mangles em-dashes when serialising) — explicit override at runtime.
    STATUS_OPTIONS[2][0][1] = '— None (not in list) —';

    var currentId = null;

    function openModal(id, name) {
        currentId = id;
        titleEl.textContent = name || 'Manage entry';
        loadingEl.hidden = false;
        formEl.hidden = true;
        errorEl.hidden = true;
        modal.hidden = false;
        backdrop.hidden = false;
        document.body.classList.add('modal-open');

        fetch('/api/library/entry?id=' + encodeURIComponent(id), {
            credentials: 'same-origin',
            headers: { 'Accept': 'application/json' },
        })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (!data || !data.success) {
                    showError('Failed to load entry. Try again.');
                    return;
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
                loadingEl.hidden = true;
                formEl.hidden = false;
            })
            .catch(function () {
                showError('Network error loading entry.');
            });
    }

    function closeModal() {
        modal.hidden = true;
        backdrop.hidden = true;
        document.body.classList.remove('modal-open');
        currentId = null;
        saveBtn.disabled = false;
    }

    function showError(msg) {
        loadingEl.hidden = true;
        errorEl.textContent = msg;
        errorEl.hidden = false;
        formEl.hidden = false;
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

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && !modal.hidden) closeModal();
    });

    formEl.addEventListener('submit', function (e) {
        e.preventDefault();
        if (!currentId) return;
        saveBtn.disabled = true;
        errorEl.hidden = true;

        var payload = {
            id: currentId,
            status: statusSelect.value,
            progress: parseInt(progressInput.value || '0', 10),
            score: scoreInput.value ? parseFloat(scoreInput.value) : null,
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
