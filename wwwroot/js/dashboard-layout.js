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
        try { saved = JSON.parse(localStorage.getItem(KEY)) || []; } catch (_) { saved = []; }
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

    function renderRows() {
        if (!list) return;
        list.innerHTML = '';
        // Only offer sections that actually rendered (gated by enabled modes).
        var rows = layout.filter(function (e) { return present(e.key); });
        rows.forEach(function (entry, i) {
            var li = document.createElement('li');
            li.className = 'dash-modal-row' + (entry.visible ? '' : ' is-hidden');

            var up = document.createElement('button');
            up.type = 'button'; up.className = 'dash-row-move'; up.setAttribute('aria-label', 'Move up');
            up.textContent = '↑'; up.disabled = i === 0;
            up.addEventListener('click', function () { move(entry.key, -1); });

            var down = document.createElement('button');
            down.type = 'button'; down.className = 'dash-row-move'; down.setAttribute('aria-label', 'Move down');
            down.textContent = '↓'; down.disabled = i === rows.length - 1;
            down.addEventListener('click', function () { move(entry.key, 1); });

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

            li.appendChild(up);
            li.appendChild(down);
            li.appendChild(name);
            li.appendChild(toggle);
            list.appendChild(li);
        });
    }

    // Move a key one step among the PRESENT rows (so disabled-by-mode keys
    // between them don't swallow the step), then persist + re-apply.
    function move(key, dir) {
        var presentKeys = layout.filter(function (e) { return present(e.key); }).map(function (e) { return e.key; });
        var pi = presentKeys.indexOf(key);
        var ni = pi + dir;
        if (pi < 0 || ni < 0 || ni >= presentKeys.length) return;
        var swapKey = presentKeys[ni];
        var ai = layout.findIndex(function (e) { return e.key === key; });
        var bi = layout.findIndex(function (e) { return e.key === swapKey; });
        var tmp = layout[ai]; layout[ai] = layout[bi]; layout[bi] = tmp;
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
})();
