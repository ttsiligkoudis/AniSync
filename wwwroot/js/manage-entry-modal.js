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
    // The element that had focus before the modal opened — restored on
    // close so keyboard users return to where they left off.
    var lastFocused = null;

    // Focusable selector used by the trap — a CSS-side allowlist of every
    // element type that should participate in Tab cycling inside the modal.
    var FOCUSABLE_SEL = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

    function openModal(id, name, card) {
        // Remember which element opened the modal so close can restore focus
        // there. Falls back to body if there's no active element (rare —
        // would require the modal to be triggered by something other than a
        // click handler).
        lastFocused = document.activeElement;

        titleEl.textContent = name || 'Manage entry';
        loadingEl.hidden = false;
        formEl.hidden = true;
        errorEl.hidden = true;
        modal.hidden = false;
        backdrop.hidden = false;
        document.body.classList.add('modal-open');
        setBackgroundInert(true);

        // Reset season picker; will be re-populated by loadEntry if applicable.
        seasonField.hidden = true;
        activeCard = card || null;

        // Focus the close (×) button so keyboard / screen-reader users land
        // somewhere predictable inside the dialog. The form is still loading
        // — once it appears, the user can Tab into the inputs naturally.
        // Wrapped in setTimeout to give the browser a paint cycle so the
        // .focus() call doesn't race the modal's reveal transition.
        setTimeout(function () { closeBtn.focus(); }, 30);

        loadEntry(id, /* isInitial */ true);
    }

    function closeModal() {
        modal.hidden = true;
        backdrop.hidden = true;
        document.body.classList.remove('modal-open');
        setBackgroundInert(false);
        activeEntryId = null;
        activeService = null;
        activeCard = null;
        activeOriginalStatus = null;
        saveBtn.disabled = false;

        // Hand focus back to the element that opened the modal. Skips when
        // that element is no longer in the DOM (e.g., the card was removed
        // by the optimistic update post-save) — fall back to body so focus
        // doesn't get stuck on a detached node.
        if (lastFocused && document.contains(lastFocused) &&
            typeof lastFocused.focus === 'function') {
            try { lastFocused.focus(); } catch (e) { /* ignore */ }
        }
        lastFocused = null;
    }

    // Mark every body-level element except the modal + backdrop as inert
    // while the modal is open, so background interactives are unfocusable
    // (Tab stays inside the modal) AND hidden from screen readers (so the
    // SR reading order doesn't drift past the dialog). Modern browsers
    // (Chrome 102+, Firefox 112+, Safari 15.5+) all support [inert]; older
    // ones gracefully degrade to "still focusable but covered by backdrop".
    function setBackgroundInert(on) {
        var children = document.body.children;
        for (var i = 0; i < children.length; i++) {
            var child = children[i];
            if (child === modal || child === backdrop) continue;
            // Toast container too — toasts during modal-open shouldn't
            // steal the SR cursor; they remain visible but inert.
            if (on) child.setAttribute('inert', '');
            else child.removeAttribute('inert');
        }
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
        // Honour prefers-reduced-motion — skip the 280ms transform/opacity
        // animation and remove the card immediately. The CSS @media rule
        // already nulls transitions globally, but inline styles below set
        // explicit transition properties that would override the CSS, so
        // we have to short-circuit at the JS layer too.
        var reduceMotion = window.matchMedia &&
            window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        if (reduceMotion) {
            if (card.parentNode) card.parentNode.removeChild(card);
            return;
        }

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

    // +1 episode quick-action — bumps the user's progress by 1 without
    // opening the modal. Reuses /api/library/entry/save with the existing
    // status preserved (read from data-meta-status), progress incremented.
    // Optimistic in-place update of the progress badge + dashboard stats,
    // matching the modal save flow's behaviour.
    function bumpProgress(plusBtn, card) {
        var id = card.getAttribute('data-meta-id');
        var status = card.getAttribute('data-meta-status');
        var currentProgress = parseInt(card.getAttribute('data-meta-progress') || '0', 10);
        var totalEpisodes = parseInt(card.getAttribute('data-meta-total') || '0', 10) || null;
        if (!id || !status) return;

        // Don't let the user push progress above the show's total — most
        // services reject it anyway. If they're at the cap, +1 is a no-op
        // (user should mark Completed via the modal instead).
        if (totalEpisodes && currentProgress >= totalEpisodes) {
            if (window.AniSyncToast) window.AniSyncToast.show('Already at last episode');
            return;
        }

        var newProgress = currentProgress + 1;
        plusBtn.disabled = true;

        fetch('/api/library/entry/save', {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'Content-Type': 'application/json',
                'Accept': 'application/json',
            },
            body: JSON.stringify({
                id: id,
                status: status,
                progress: newProgress,
                // Other fields left null — the SaveEntryRequest accepts that
                // and the per-service save methods preserve existing values
                // for unspecified fields rather than wiping them.
            }),
        })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data && data.success) {
                    if (window.AniSyncToast) window.AniSyncToast.show('+1 ep');
                    refreshCardProgress(card, newProgress, totalEpisodes);
                    // Persist on the card itself so subsequent clicks see the
                    // updated value (data attr is the source of truth here).
                    card.setAttribute('data-meta-progress', String(newProgress));
                } else {
                    if (window.AniSyncToast) window.AniSyncToast.show('Save failed');
                }
            })
            .catch(function () {
                if (window.AniSyncToast) window.AniSyncToast.show('Network error');
            })
            .finally(function () {
                plusBtn.disabled = false;
            });
    }

    // Quick-mark-watched on /anime/{id}'s episode list. Bumps progress to
    // the clicked episode's number (preserving status), updates checkmarks
    // on rows ≤ new progress, refreshes the user-state panel in the hero,
    // and bumps the dashboard stats if applicable. No reload — same
    // optimistic pattern the +1 quick-action uses on cards.
    function markEpisodeWatched(row, list) {
        var newProgress = parseInt(row.getAttribute('data-episode-num'), 10);
        var currentProgress = parseInt(list.getAttribute('data-current-progress') || '0', 10);
        if (!newProgress || newProgress === currentProgress) return;

        var id = list.getAttribute('data-meta-id');
        var status = list.getAttribute('data-current-status');
        if (!id || !status) return;

        // Disable interaction while the request is in flight; a single
        // listener on document keeps clicks deduped without per-row state.
        list.classList.add('anime-detail-episodes-busy');

        fetch('/api/library/entry/save', {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'Content-Type': 'application/json',
                'Accept': 'application/json',
            },
            body: JSON.stringify({ id: id, status: status, progress: newProgress }),
        })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (!data || !data.success) {
                    if (window.AniSyncToast) window.AniSyncToast.show('Save failed');
                    return;
                }
                // Update checkmark classes on every row (handles both
                // bumping forward and rare back-step). Also persist the new
                // current-progress on the list so subsequent clicks compute
                // deltas correctly without re-reading from the DOM tree.
                list.setAttribute('data-current-progress', String(newProgress));
                Array.prototype.forEach.call(list.querySelectorAll('li.anime-detail-episode'), function (li) {
                    var num = parseInt(li.getAttribute('data-episode-num') || '0', 10);
                    if (num > 0 && num <= newProgress) {
                        li.classList.add('anime-detail-episode-watched');
                    } else {
                        li.classList.remove('anime-detail-episode-watched');
                    }
                });
                refreshDetailUserState(newProgress);
                if (window.AniSyncToast) window.AniSyncToast.show('Watched ep ' + newProgress);
            })
            .catch(function () {
                if (window.AniSyncToast) window.AniSyncToast.show('Network error');
            })
            .finally(function () {
                list.classList.remove('anime-detail-episodes-busy');
            });
    }

    // Update the hero's user-state strip ("Watching · Ep 5/12 · Your score:
    // 8.0") to reflect the new progress without a page reload. The strip is
    // server-rendered with " · "-joined bits; we look for the "Ep N/M" or
    // "Ep N" chunk and replace it. Best-effort — the next full page render
    // produces the canonical version.
    function refreshDetailUserState(newProgress) {
        var stateEl = document.querySelector('.anime-detail-user-state');
        if (!stateEl) return;
        var text = stateEl.textContent;
        var replaced = text.replace(/Ep \d+(\s*\/\s*\d+)?/, function (match) {
            var totalMatch = match.match(/\/\s*(\d+)/);
            return totalMatch ? 'Ep ' + newProgress + ' / ' + totalMatch[1] : 'Ep ' + newProgress;
        });
        if (replaced !== text) stateEl.textContent = replaced;
    }

    // Click interception. Four hooks live on the document:
    //   1. button.library-card-plus inside a card → +1 quick-action.
    //      preventDefault + stopPropagation so the parent anchor doesn't
    //      navigate to the detail page on this click.
    //   2. button.season-tab on the detail page → switch which season's
    //      episodes are visible. Pure DOM-toggle, no fetch.
    //   3. li.anime-detail-episode inside a click-enabled list → quick-
    //      mark-watched: bumps progress to that episode's number.
    //   4. [data-open-modal] anywhere (typically the Edit button on the
    //      /anime/{id} detail page) → open the modal for that meta id.
    //
    // Cards themselves are <a href="/anime/{id}"> and click-navigate to the
    // detail page by default — no JS interception. The modal is reserved
    // for explicit edit-intent actions (Edit button, +1 quick-action,
    // episode click) so the card surface stays predictable.
    document.addEventListener('click', function (e) {
        var plusBtn = e.target.closest && e.target.closest('button.library-card-plus');
        if (plusBtn) {
            e.preventDefault();
            e.stopPropagation();
            var owningCard = plusBtn.closest('a.library-card[data-meta-id]');
            if (owningCard) bumpProgress(plusBtn, owningCard);
            return;
        }
        var seasonTab = e.target.closest && e.target.closest('button.season-tab[data-season-num]');
        if (seasonTab) {
            e.preventDefault();
            switchSeason(seasonTab);
            return;
        }
        var synopsisToggle = e.target.closest && e.target.closest('button.anime-detail-synopsis-toggle');
        if (synopsisToggle) {
            e.preventDefault();
            var wrap = synopsisToggle.closest('.anime-detail-synopsis-wrap');
            if (wrap) {
                var nowCollapsed = wrap.getAttribute('data-collapsed') === 'true';
                if (nowCollapsed) {
                    wrap.removeAttribute('data-collapsed');
                    synopsisToggle.textContent = 'Show less';
                    synopsisToggle.setAttribute('aria-expanded', 'true');
                } else {
                    wrap.setAttribute('data-collapsed', 'true');
                    synopsisToggle.textContent = 'Show more';
                    synopsisToggle.setAttribute('aria-expanded', 'false');
                }
            }
            return;
        }
        var episodeRow = e.target.closest && e.target.closest('li.anime-detail-episode[data-episode-num]');
        if (episodeRow) {
            var list = episodeRow.closest('ol.anime-detail-episodes[data-can-edit="true"]');
            if (list) {
                e.preventDefault();
                markEpisodeWatched(episodeRow, list);
                return;
            }
        }
        var modalTrigger = e.target.closest && e.target.closest('[data-open-modal][data-meta-id]');
        if (modalTrigger) {
            e.preventDefault();
            openModal(modalTrigger.getAttribute('data-meta-id'),
                      modalTrigger.getAttribute('data-meta-name'),
                      /* card */ null);
        }
    });

    // Season tab toggle — pure DOM swap. Updates the active class on the
    // tab strip and shows/hides episode rows by data-season-num. Doesn't
    // refetch anything; the full episode list is server-rendered in one
    // <ol> and the tabs just filter visibility.
    function switchSeason(tab) {
        var seasonNum = tab.getAttribute('data-season-num');
        if (!seasonNum) return;
        var nav = tab.closest('.season-tabs');
        var list = document.querySelector('ol.anime-detail-episodes');
        if (!nav || !list) return;
        // Active class on tabs
        Array.prototype.forEach.call(nav.querySelectorAll('.season-tab'), function (t) {
            var isActive = t === tab;
            t.classList.toggle('season-tab-active', isActive);
            t.setAttribute('aria-selected', isActive ? 'true' : 'false');
        });
        // Show/hide rows
        list.setAttribute('data-active-season', seasonNum);
        Array.prototype.forEach.call(list.querySelectorAll('li.anime-detail-episode'), function (li) {
            var rowSeason = li.getAttribute('data-season-num');
            li.classList.toggle('anime-detail-episode-hidden', rowSeason !== seasonNum);
        });
    }

    closeBtn.addEventListener('click', closeModal);
    cancelBtn.addEventListener('click', closeModal);
    backdrop.addEventListener('click', closeModal);

    seasonSelect.addEventListener('change', function () {
        loadEntry(seasonSelect.value, /* isInitial */ false);
    });

    document.addEventListener('keydown', function (e) {
        if (modal.hidden) return;
        if (e.key === 'Escape') {
            closeModal();
            return;
        }
        // Focus trap — Tab/Shift+Tab cycles within the modal's focusable
        // elements. Backstop for the [inert] background: even when inert
        // unfocuses background elements, the browser would otherwise hand
        // focus to its own URL bar / chrome on Tab past the last focusable.
        if (e.key === 'Tab') {
            var focusables = Array.prototype.filter.call(
                modal.querySelectorAll(FOCUSABLE_SEL),
                function (el) {
                    // Skip elements that are visually hidden / inside a
                    // hidden field-group (e.g. the season selector when
                    // not applicable) — those shouldn't be tab stops.
                    return !el.disabled && el.offsetParent !== null;
                }
            );
            if (!focusables.length) return;
            var first = focusables[0];
            var last = focusables[focusables.length - 1];
            if (e.shiftKey && document.activeElement === first) {
                e.preventDefault();
                last.focus();
            } else if (!e.shiftKey && document.activeElement === last) {
                e.preventDefault();
                first.focus();
            }
        }
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
                    if (cardForUpdate) {
                        // Card-context save — optimistic in-place update +
                        // toast inline. Scroll position preserved.
                        showToast('Saved');
                        var filter = getPageListFilter();
                        var stays = statusBelongsHere(serviceForUpdate, newStatus, filter);
                        if (!stays) {
                            fadeOutCard(cardForUpdate);
                        } else {
                            refreshCardProgress(cardForUpdate, newProgress, totalForCard);
                        }
                        adjustStatsForTransition(serviceForUpdate, originalStatusForUpdate, newStatus, totalForCard);
                        closeModal();
                    } else {
                        // No card → modal opened from the detail page Edit
                        // button (or any future non-card entry point). The
                        // page's user-state panel / hero meta / etc. would
                        // go stale if we just close. Reload so it catches
                        // up with server state; queue the toast through
                        // sessionStorage so it survives the navigation.
                        try { sessionStorage.setItem('anisync-toast', 'Saved'); }
                        catch (e) { /* private-browsing — toast is best-effort */ }
                        window.location.reload();
                    }
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
