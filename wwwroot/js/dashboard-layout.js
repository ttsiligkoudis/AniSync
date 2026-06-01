// Dashboard layout customisation. Lets the user reorder the dashboard sections
// and toggle their visibility. The layout is stored in localStorage and applied
// client-side — the server still renders every section (gated by the enabled
// media types); this just reorders/hides them in the DOM. Default order is the
// server's render order, so an untouched dashboard looks exactly as before.
(function () {
    var KEY = 'anisync.dashboardLayout.v1';

    // Every customisable section, in DEFAULT order, with a label for the modal.
    // Keys match the [data-dash-section] attributes the view stamps. A key may
    // map to more than one section (the per-type video shelves share a key);
    // they move + hide together and keep their on-page order.
    var UNITS = [
        { key: 'anime-stats',        label: 'Your stats · Anime' },
        { key: 'anime-continue',     label: 'Continue watching · Anime' },
        { key: 'anime-this-season',  label: 'This Season' },
        { key: 'anime-new-episodes', label: 'New Episodes Today' },
        { key: 'anime-popular',      label: 'Most Popular this Season' },
        { key: 'anime-anticipated',  label: 'Most Anticipated · Anime' },
        { key: 'video-stats',        label: 'Your stats · Trakt' },
        { key: 'video-continue',     label: 'Continue watching · Movies/Series' },
        { key: 'video-trending',     label: 'Trending · Movies/Series' },
        { key: 'video-popular',      label: 'Most Popular · Movies/Series' },
        { key: 'video-anticipated',  label: 'Most Anticipated · Movies/Series' },
        { key: 'browse',             label: 'Browse By' },
    ];
    var DEFAULT_ORDER = UNITS.map(function (u) { return u.key; });
    var LABELS = {}; UNITS.forEach(function (u) { LABELS[u.key] = u.label; });

    // ── persistence ──
    // Stored as [{ key, visible }]. Merge with the defaults so a key added in a
    // later build (or one the saved layout never knew about) shows up at the end
    // rather than vanishing.
    function load() {
        var saved = [];
        // Account layout (logged-in, bootstrapped by the server) is the source of
        // truth so it follows the user across devices; otherwise localStorage.
        var boot = (window.AniSync && window.AniSync.settings && window.AniSync.settings.dashboardLayout) || null;
        var raw = boot;
        if (!raw) { try { raw = localStorage.getItem(KEY); } catch (_) { raw = null; } }
        try { saved = JSON.parse(raw) || []; } catch (_) { saved = []; }
        if (boot) { try { localStorage.setItem(KEY, boot); } catch (_) { /* mirror locally */ } }
        if (!Array.isArray(saved)) saved = [];
        var seen = {};
        var out = [];
        saved.forEach(function (e) {
            if (e && DEFAULT_ORDER.indexOf(e.key) !== -1 && !seen[e.key]) {
                seen[e.key] = true;
                out.push({ key: e.key, visible: e.visible !== false });
            }
        });
        DEFAULT_ORDER.forEach(function (k) {
            if (!seen[k]) { seen[k] = true; out.push({ key: k, visible: true }); }
        });
        return out;
    }
    function save(layout) {
        try { localStorage.setItem(KEY, JSON.stringify(layout)); } catch (_) { /* private mode */ }
    }

    // Section elements present on this page, grouped by key (a key → 1+ elements).
    function sectionsByKey() {
        var map = {};
        document.querySelectorAll('[data-dash-section]').forEach(function (el) {
            var k = el.getAttribute('data-dash-section');
            (map[k] || (map[k] = [])).push(el);
        });
        return map;
    }

    // Reorder + show/hide the live DOM to match the layout.
    function apply(layout) {
        var byKey = sectionsByKey();
        var present = Object.keys(byKey);
        if (!present.length) return;

        // Anchor: where the first customisable section currently sits. We cluster
        // the ordered sections there; interspersed loader <script>s stay put and
        // keep working (they query their section by selector, not by position).
        var firstEl = document.querySelector('[data-dash-section]');
        var parent = firstEl.parentNode;
        var marker = document.createComment('dash-layout');
        parent.insertBefore(marker, firstEl);

        layout.forEach(function (entry) {
            (byKey[entry.key] || []).forEach(function (el) {
                el.hidden = !entry.visible;
                parent.insertBefore(el, marker);
            });
        });
        parent.removeChild(marker);
    }

    var layout = load();
    apply(layout);

    // ── customise modal ──
    var modal = document.querySelector('[data-dash-modal]');
    var backdrop = document.querySelector('[data-dash-modal-backdrop]');
    var list = modal && modal.querySelector('[data-dash-modal-list]');

    function present(key) {
        return document.querySelector('[data-dash-section="' + key + '"]') != null;
    }

    // Current dashboard media-type filter (set by the media-type switch). When
    // it's a specific mode (not "all"), the customize modal should only offer
    // that mode's sections.
    function dashFilter() {
        try { return localStorage.getItem('anisync-dashboard-media') || 'all'; }
        catch (_) { return 'all'; }
    }
    // A section key is offered under the active filter when it has at least one
    // element matching the filter — or an untyped element (general sections like
    // "Browse By" always show). filter === "all" shows everything present.
    function presentForFilter(key, filter) {
        var els = document.querySelectorAll('[data-dash-section="' + key + '"]');
        if (els.length === 0) return false;
        if (filter === 'all') return true;
        for (var i = 0; i < els.length; i++) {
            var mt = els[i].getAttribute('data-media-type');
            if (!mt) return true;
            if (mt.split(/\s+/).indexOf(filter) !== -1) return true;
        }
        return false;
    }

    // On the dashboard, only offer sections that actually rendered (gated by the
    // enabled modes) AND match the active media-type filter. On /account there
    // are no dashboard sections present, so offer every section so the user can
    // still configure the layout there.
    function pageHasSections() { return document.querySelector('[data-dash-section]') != null; }
    function displayedRows() {
        if (!pageHasSections()) return layout.slice();
        var filter = dashFilter();
        return layout.filter(function (e) { return presentForFilter(e.key, filter); });
    }

    function renderRows() {
        if (!list) return;
        list.innerHTML = '';
        var rows = displayedRows();
        rows.forEach(function (entry) {
            var li = document.createElement('li');
            li.className = 'dash-modal-row' + (entry.visible ? '' : ' is-hidden');
            li.setAttribute('data-key', entry.key);

            // Drag handle — the only reorder trigger (pointer events below),
            // matching the stream-addon list. Touch + mouse via one path.
            var handle = document.createElement('span');
            handle.className = 'dash-row-handle';
            handle.setAttribute('aria-label', 'Drag to reorder');
            handle.setAttribute('title', 'Drag to reorder');
            handle.innerHTML = '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="3" y1="9" x2="21" y2="9"/><line x1="3" y1="15" x2="21" y2="15"/></svg>';

            var name = document.createElement('span');
            name.className = 'dash-row-label';
            name.textContent = LABELS[entry.key] || entry.key;

            var toggle = document.createElement('button');
            toggle.type = 'button';
            toggle.className = 'dash-row-toggle';
            toggle.setAttribute('aria-pressed', entry.visible ? 'true' : 'false');
            toggle.setAttribute('aria-label', entry.visible ? 'Hide section' : 'Show section');
            toggle.textContent = entry.visible ? 'Shown' : 'Hidden';
            toggle.addEventListener('click', function () { toggleVisible(entry.key); });

            li.appendChild(handle);
            li.appendChild(name);
            li.appendChild(toggle);
            list.appendChild(li);
        });
    }

    // Reorder the layout to match a new on-screen key order from a drag. Only
    // the displayed keys move; non-displayed entries (hidden by mode) keep their
    // slots, and every entry keeps its visible flag.
    function applyDraggedOrder(newKeys) {
        var byKey = {};
        layout.forEach(function (e) { byKey[e.key] = e; });
        var inDrag = {};
        newKeys.forEach(function (k) { inDrag[k] = true; });
        var queue = newKeys.slice();
        layout = layout.map(function (e) {
            return inDrag[e.key] ? byKey[queue.shift()] : e;
        });
        commit();
    }
    function toggleVisible(key) {
        var e = layout.find(function (x) { return x.key === key; });
        if (e) e.visible = !e.visible;
        commit();
    }
    function commit() {
        save(layout);
        apply(layout);
        renderRows();
        // Persist to the account too (logged-in) so the layout follows the user
        // across devices. Fire-and-forget; localStorage already has it.
        if (window.AniSync && window.AniSync.loggedIn) {
            try {
                fetch('/Home/SetDashboardLayout', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: {
                        'Content-Type': 'application/x-www-form-urlencoded',
                        'X-Requested-With': 'XMLHttpRequest'
                    },
                    body: 'layout=' + encodeURIComponent(JSON.stringify(layout)),
                    skipLoader: true,
                    keepalive: true
                });
            } catch (_) { /* best-effort */ }
        }
    }

    function openModal() {
        if (!modal) return;
        renderRows();
        modal.hidden = false;
        if (backdrop) backdrop.hidden = false;
        document.body.classList.add('dash-modal-open');
    }
    function closeModal() {
        if (!modal) return;
        modal.hidden = true;
        if (backdrop) backdrop.hidden = true;
        document.body.classList.remove('dash-modal-open');
    }

    document.querySelectorAll('[data-dash-customize-open]').forEach(function (el) {
        el.addEventListener('click', function () { openModal(); });
    });
    if (modal) {
        var closeBtn = modal.querySelector('[data-dash-modal-close]');
        var doneBtn = modal.querySelector('[data-dash-modal-done]');
        var resetBtn = modal.querySelector('[data-dash-reset]');
        if (closeBtn) closeBtn.addEventListener('click', closeModal);
        if (doneBtn) doneBtn.addEventListener('click', closeModal);
        if (resetBtn) resetBtn.addEventListener('click', function () {
            layout = DEFAULT_ORDER.map(function (k) { return { key: k, visible: true }; });
            commit();
        });
    }
    if (backdrop) backdrop.addEventListener('click', closeModal);
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && modal && !modal.hidden) closeModal();
    });

    // ── drag-to-reorder (mirrors the stream-addon list) ──
    // Pointer events on the row's drag handle, captured so they keep firing as
    // the finger/mouse moves; a drop indicator marks the target gap; on release
    // the row is moved and the new order persisted. One path for mouse + touch.
    if (list) {
        var drag = null;
        var rowsIn = function () {
            return Array.prototype.slice.call(list.querySelectorAll('.dash-modal-row'));
        };
        var clearIndicators = function () {
            rowsIn().forEach(function (r) { r.classList.remove('drop-above', 'drop-below'); });
        };
        var findDrop = function (clientY) {
            var rows = rowsIn().filter(function (r) { return !r.classList.contains('is-dragging'); });
            for (var i = 0; i < rows.length; i++) {
                var rect = rows[i].getBoundingClientRect();
                if (clientY < rect.top) return { row: rows[i], before: true };
                if (clientY <= rect.bottom) return { row: rows[i], before: clientY < rect.top + rect.height / 2 };
            }
            return rows.length ? { row: rows[rows.length - 1], before: false } : null;
        };

        list.addEventListener('pointerdown', function (e) {
            var handle = e.target.closest ? e.target.closest('.dash-row-handle') : null;
            if (!handle) return;
            if (e.button !== undefined && e.button !== 0) return;
            var row = handle.closest('.dash-modal-row');
            if (!row) return;
            e.preventDefault();
            drag = { row: row, handle: handle, pointerId: e.pointerId };
            row.classList.add('is-dragging');
            try { handle.setPointerCapture(e.pointerId); } catch (_) { /* old browsers */ }
        });
        list.addEventListener('pointermove', function (e) {
            if (!drag) return;
            e.preventDefault();
            clearIndicators();
            var drop = findDrop(e.clientY);
            if (drop) drop.row.classList.add(drop.before ? 'drop-above' : 'drop-below');
        });
        var endDrag = function (e) {
            if (!drag) return;
            var row = drag.row, handle = drag.handle;
            var drop = (e && typeof e.clientY === 'number') ? findDrop(e.clientY) : null;
            if (drop) {
                if (drop.before) list.insertBefore(row, drop.row);
                else list.insertBefore(row, drop.row.nextSibling);
            }
            row.classList.remove('is-dragging');
            clearIndicators();
            try { handle.releasePointerCapture(drag.pointerId); } catch (_) { }
            drag = null;
            // Commit the new on-screen order (rebuilds the rows).
            applyDraggedOrder(rowsIn().map(function (r) { return r.getAttribute('data-key'); }));
        };
        list.addEventListener('pointerup', endDrag);
        list.addEventListener('pointercancel', endDrag);
    }
})();
