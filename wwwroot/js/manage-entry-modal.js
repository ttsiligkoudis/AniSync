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
//   4. On success: show "Saved" toast inline + optimistically update the
//      clicked card (refresh progress badge) or fade-and-remove it when the
//      new status no longer matches the current page's list filter. No page
//      reload — keeps scroll position and feels like a real app.
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

    // Per-service status string → AniSync's tab slug (the value of ?list= on
    // /library). Used post-save to decide whether the edited card should
    // remain in the current view or fade out (its status moved out of the
    // tab's filter, e.g. user marked Watching → Completed while on the
    // Watching tab — card no longer belongs here).
    var SERVICE_STATUS_TO_FILTER = {
        0: { 'current': 'current', 'planned': 'planning', 'completed': 'completed', 'on_hold': 'paused', 'dropped': 'dropped' },
        1: { 'CURRENT': 'current', 'PLANNING': 'planning', 'COMPLETED': 'completed', 'PAUSED': 'paused', 'DROPPED': 'dropped', 'REPEATING': 'repeating' },
        2: { 'watching': 'current', 'plan_to_watch': 'planning', 'completed': 'completed', 'on_hold': 'paused', 'dropped': 'dropped', 'rewatching': 'repeating' },
    };

    // The currently-active per-cour entry id (anilist:N / kitsu:N / mal:N).
    // Distinct from the card's original id, which might be a cross-service
    // imdb:/tmdb: id that resolves to one of multiple per-cour entries. Save
    // payloads send activeEntryId, not the card id.
    var activeEntryId = null;
    // Service of the currently-loaded entry — needed for the save-side
    // status→filter mapping, since each service uses different status enums.
    var activeService = null;
    // The DOM card that opened the modal. Held so we can update or remove
    // it after save without a full page reload.
    var activeCard = null;
    // The status the entry had AT LOAD time (before user edits). The save
    // handler diffs this against the new status to figure out which stat
    // counters to bump/decrement.
    var activeOriginalStatus = null;

    function openModal(id, name, card) {
        titleEl.textContent = name || 'Manage entry';
        loadingEl.hidden = false;
        formEl.hidden = true;
        errorEl.hidden = true;
        modal.hidden = false;
        backdrop.hidden = false;
        document.body.classList.add('modal-open');
        // Reset season picker; will be re-populated by loadEntry if applicable.
        seasonField.hidden = true;
        activeCard = card || null;
        loadEntry(id, /* isInitial */ true);
    }

    function closeModal() {
        modal.hidden = true;
        backdrop.hidden = true;
        document.body.classList.remove('modal-open');
        activeEntryId = null;
        activeService = null;
        activeCard = null;
        activeOriginalStatus = null;
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
                activeEntryId = data.selectedEntryId || id;
                activeService = data.service;
                activeOriginalStatus = data.status || null;

                // Seasons dropdown: only render when the franchise has 2+
                // mappings. Single-mapping responses don't need a picker.
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

    // What list-status filter does the current page apply to its cards?
    //   /library?list=X  → X (defaults to "current")
    //   /                → "current" (Continue Watching shelf is implicitly Watching)
    //   /discover, etc.  → null (no list-status filter — cards stay regardless)
    function getPageListFilter() {
        var path = window.location.pathname;
        var params = new URLSearchParams(window.location.search);
        if (path === '/library' || path === '/library/') {
            return (params.get('list') || 'current').toLowerCase();
        }
        if (path === '/' || path === '') {
            return 'current';
        }
        return null;
    }

    function statusBelongsHere(service, savedStatus, filter) {
        if (!filter) return true;            // no filter — card stays regardless
        if (!savedStatus) return false;      // status="" = entry deleted, never belongs
        var map = SERVICE_STATUS_TO_FILTER[service];
        return map ? map[savedStatus] === filter : true;
    }

    function fadeOutCard(card) {
        // Smoothly collapse the card so the grid rows don't jump under the
        // user's eye. Width:0 + opacity:0 + the existing transition makes the
        // surrounding cards reflow into the gap.
        card.style.transition = 'opacity 0.25s, transform 0.25s';
        card.style.opacity = '0';
        card.style.transform = 'scale(0.92)';
        setTimeout(function () {
            if (card.parentNode) card.parentNode.removeChild(card);
        }, 280);
    }

    function refreshCardProgress(card, progress, totalEpisodes) {
        // The progress badge sits inside .library-card-poster-wrap (top-left)
        // alongside the score badge (bottom-left). Add it if missing, update
        // text if present, remove if progress was zeroed.
        var wrap = card.querySelector('.library-card-poster-wrap');
        if (!wrap) return;
        var existing = wrap.querySelector('.library-card-progress');

        if (!progress || progress <= 0) {
            if (existing) existing.parentNode.removeChild(existing);
            return;
        }

        var label = totalEpisodes && totalEpisodes > 0
            ? progress + ' / ' + totalEpisodes
            : 'Ep ' + progress;

        if (existing) {
            existing.textContent = label;
        } else {
            var span = document.createElement('span');
            span.className = 'library-card-progress';
            span.textContent = label;
            wrap.appendChild(span);
        }
    }

    function showToast(text) {
        if (window.AniSyncToast && window.AniSyncToast.show) {
            window.AniSyncToast.show(text);
        }
    }

    // Stat-cell adjustment — keeps the dashboard's Watching / Completed /
    // Hours numbers in sync with optimistic card removals/additions. No-ops
    // gracefully when the dashboard isn't on the current page (Library /
    // Discover have no stats panel — querySelector returns null).
    function adjustStat(name, delta) {
        var num = document.querySelector('.stats-cell[data-stat="' + name + '"] .stats-number');
        if (!num) return;
        // Strip locale separators (commas) before parsing so "1,234" → 1234.
        var current = parseInt(num.textContent.replace(/[^\d-]/g, ''), 10) || 0;
        var next = Math.max(0, current + delta);
        num.textContent = next.toLocaleString();
    }

    // Hours bucket adjusts in lockstep with Completed when the entry has a
    // known total-episodes count. Uses the same 24-min/episode assumption
    // HomeController.Index applies server-side so the client-side delta
    // matches the next full render.
    function adjustHours(episodesDelta) {
        var num = document.querySelector('.stats-cell[data-stat="hours"] .stats-number');
        if (!num) return;
        var current = parseInt(num.textContent.replace(/[^\d-]/g, ''), 10) || 0;
        var next = Math.max(0, current + Math.round(episodesDelta * 24 / 60));
        num.textContent = next.toLocaleString();
    }

    // Translate per-service status → AniSync filter slug, returning null for
    // unknown / empty status. Used by the stat-adjustment logic to detect
    // bucket transitions (Watching → Completed etc.).
    function statusToFilter(service, status) {
        if (!status) return null;
        var map = SERVICE_STATUS_TO_FILTER[service];
        return map ? (map[status] || null) : null;
    }

    function adjustStatsForTransition(service, oldStatus, newStatus, totalEpisodes) {
        var oldFilter = statusToFilter(service, oldStatus);
        var newFilter = statusToFilter(service, newStatus);
        if (oldFilter === newFilter) return;

        if (oldFilter === 'current') adjustStat('watching', -1);
        if (oldFilter === 'completed') {
            adjustStat('completed', -1);
            if (totalEpisodes) adjustHours(-totalEpisodes);
        }
        if (newFilter === 'current') adjustStat('watching', 1);
        if (newFilter === 'completed') {
            adjustStat('completed', 1);
            if (totalEpisodes) adjustHours(totalEpisodes);
        }
    }

    // Card click interception. Targets only <a class="library-card"> elements
    // that carry a data-meta-id (the link variant; inert anonymous cards are
    // <div>s and don't match). Falls back to default navigation if the data
    // attribute is missing — keeps the JS as progressive enhancement.
    document.addEventListener('click', function (e) {
        var card = e.target.closest && e.target.closest('a.library-card[data-meta-id]');
        if (!card) return;
        e.preventDefault();
        openModal(card.getAttribute('data-meta-id'), card.getAttribute('data-meta-name'), card);
    });

    closeBtn.addEventListener('click', closeModal);
    cancelBtn.addEventListener('click', closeModal);
    backdrop.addEventListener('click', closeModal);

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

        var newProgress = parseInt(progressInput.value || '0', 10);
        var newStatus = statusSelect.value;
        var totalForCard = parseInt(progressInput.max, 10) || null;

        var payload = {
            id: activeEntryId,
            status: newStatus,
            progress: newProgress,
            score: scoreInput.value ? parseFloat(scoreInput.value) : null,
            startedAt: startedInput.value || null,
            finishedAt: finishedInput.value || null,
            rewatchCount: rewatchInput.value ? parseInt(rewatchInput.value, 10) : null,
            notes: notesInput.value || null,
        };

        // Capture state for the optimistic update before closing the modal
        // (which clears activeCard / activeService / activeOriginalStatus).
        var cardForUpdate = activeCard;
        var serviceForUpdate = activeService;
        var originalStatusForUpdate = activeOriginalStatus;

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
                    showToast('Saved');

                    // Optimistic in-place card update — no page reload, scroll
                    // position preserved. If the new status no longer matches
                    // the page's list filter (e.g. user moved Watching →
                    // Completed while viewing the Watching tab), fade-and-
                    // remove the card. Otherwise refresh the progress badge.
                    if (cardForUpdate) {
                        var filter = getPageListFilter();
                        var stays = statusBelongsHere(serviceForUpdate, newStatus, filter);
                        if (!stays) {
                            fadeOutCard(cardForUpdate);
                        } else {
                            refreshCardProgress(cardForUpdate, newProgress, totalForCard);
                        }
                    }
                    // Keep the dashboard stats panel in sync with the bucket
                    // transition. No-op on /library / /discover where the
                    // panel doesn't exist.
                    adjustStatsForTransition(serviceForUpdate, originalStatusForUpdate, newStatus, totalForCard);
                    closeModal();
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
