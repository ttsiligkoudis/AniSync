// Configure-page browser helpers, loaded as an ES module via JS interop from the
// shared Configure/Advanced pages (works on both heads without touching App.razor).

export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        // Fallback for non-secure contexts / older browsers.
        try {
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            const ok = document.execCommand('copy');
            ta.remove();
            return ok;
        } catch {
            return false;
        }
    }
}

export function downloadText(filename, text, mime) {
    const blob = new Blob([text], { type: mime || 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
}

// Toggles `config-page` on <body> for the account / configure / advanced surfaces —
// the Blazor equivalent of the original views' ViewData["BodyClass"] = "config-page".
// Drives `body.config-page main { padding-bottom }` so the fixed floating save bar
// never covers the last card. Pages add it on first interactive render, remove on dispose.
export function setConfigPage(on) {
    document.body.classList.toggle('config-page', !!on);
}

// ── Streams modal open/close ──────────────────────────────────────────────
// Ports the inline <script> in _StreamsModal.cshtml: toggles the body class so
// the page locks scroll behind the modal, and wires the document-level Escape
// listener (the original closed on Esc). The dialog/backdrop visibility itself is
// driven by Blazor state (the `hidden` attribute), so this only owns the body-class
// side effect + the Esc key, which calls back into .NET to flip the state closed.
let _streamsEscHandler = null;

export function streamsModalOpen(dotnet) {
    document.body.classList.add('streams-modal-open');
    if (_streamsEscHandler) document.removeEventListener('keydown', _streamsEscHandler);
    _streamsEscHandler = function (e) {
        if (e.key === 'Escape' && dotnet) dotnet.invokeMethodAsync('CloseFromJs');
    };
    document.addEventListener('keydown', _streamsEscHandler);
}

export function streamsModalClose() {
    document.body.classList.remove('streams-modal-open');
    if (_streamsEscHandler) {
        document.removeEventListener('keydown', _streamsEscHandler);
        _streamsEscHandler = null;
    }
}

// Deep link: the "Set up streaming" nudge points at /account#streams, so the
// page asks here whether it landed on that hash and should auto-open the modal.
export function hasStreamsHash() {
    return location.hash === '#streams';
}

// ── Pointer-drag reorder (generic) ────────────────────────────────────────
// Pointer-event reorder on a per-row drag handle — a faithful port of the
// _ConfigurePageScript handler, generalised so both the stream-addon list and
// the dashboard customise modal share one code path. Single path for mouse +
// touch + pen (setPointerCapture redirects move/up to the handle even when the
// pointer drifts off it). On drop it computes the intended new order from the
// drop target — WITHOUT mutating the DOM — and hands it back to .NET via
// dotnet.invokeMethodAsync(method, order). Blazor owns the list rendering and
// re-renders from that order; mutating the DOM here would desync Blazor's keyed
// diff. Matching the original, the reorder isn't persisted on drop — the
// component stages it.
const _addonBindings = new WeakMap();

// opts: { rowSelector, handleSelector, dataAttr, method }. Defaults target the
// stream-addon list so bindAddonReorder stays a thin wrapper.
export function bindReorder(list, dotnet, opts) {
    if (!list) return;
    const rowSelector = (opts && opts.rowSelector) || '.stream-addon-row';
    const handleSelector = (opts && opts.handleSelector) || '.stream-addon-drag-handle';
    const dataAttr = (opts && opts.dataAttr) || 'data-url';
    const method = (opts && opts.method) || 'OnAddonReorder';
    // Re-bind cleanly if Blazor re-rendered the list element.
    const prev = _addonBindings.get(list);
    if (prev) prev();

    let drag = null; // { row, handle, pointerId }

    function clearIndicators() {
        list.querySelectorAll('.drop-above, .drop-below').forEach(function (el) {
            el.classList.remove('drop-above', 'drop-below');
        });
    }

    // Walks the list (excluding the dragged row) and returns the row whose
    // vertical span contains clientY, plus whether the pointer is in its top or
    // bottom half. Returns null when the list is empty.
    function findDropTarget(clientY) {
        const rows = list.querySelectorAll(rowSelector + ':not(.is-dragging)');
        for (let i = 0; i < rows.length; i++) {
            const rect = rows[i].getBoundingClientRect();
            if (clientY < rect.top) return { row: rows[i], before: true };
            if (clientY <= rect.bottom) {
                const mid = rect.top + rect.height / 2;
                return { row: rows[i], before: clientY < mid };
            }
        }
        if (rows.length > 0) return { row: rows[rows.length - 1], before: false };
        return null;
    }

    function currentOrder() {
        return Array.from(list.querySelectorAll(rowSelector))
            .map(function (r) { return r.getAttribute(dataAttr) || ''; })
            .filter(function (u) { return !!u; });
    }

    function onPointerDown(e) {
        const handle = e.target.closest(handleSelector);
        if (!handle || !list.contains(handle)) return;
        // Ignore right-click / middle-click — e.button is 0 for left/touch/pen.
        if (e.button !== undefined && e.button !== 0) return;
        const row = handle.closest(rowSelector);
        if (!row) return;
        // preventDefault on touch keeps the browser from scrolling instead.
        e.preventDefault();
        drag = { row: row, handle: handle, pointerId: e.pointerId };
        row.classList.add('is-dragging');
        try { handle.setPointerCapture(e.pointerId); } catch (_) { /* very old browsers */ }
    }

    function onPointerMove(e) {
        if (!drag) return;
        e.preventDefault();
        clearIndicators();
        const drop = findDropTarget(e.clientY);
        if (drop) drop.row.classList.add(drop.before ? 'drop-above' : 'drop-below');
    }

    function endDrag(e) {
        if (!drag) return;
        const row = drag.row;
        const handle = drag.handle;
        const before = currentOrder();
        const draggedUrl = row.getAttribute(dataAttr) || '';
        const drop = (e && typeof e.clientY === 'number') ? findDropTarget(e.clientY) : null;

        row.classList.remove('is-dragging');
        clearIndicators();
        try { handle.releasePointerCapture(drag.pointerId); } catch (_) {}
        drag = null;

        // Compute the intended order from the drop target WITHOUT moving the DOM —
        // Blazor re-renders the list from the order we hand back (see the header note).
        if (!drop || !draggedUrl) return;
        const targetUrl = drop.row.getAttribute(dataAttr) || '';
        if (!targetUrl || targetUrl === draggedUrl) return;
        const after = before.filter(function (u) { return u !== draggedUrl; });
        const idx = after.indexOf(targetUrl);
        if (idx < 0) return;
        after.splice(drop.before ? idx : idx + 1, 0, draggedUrl);

        // No-op if nothing actually moved (dropped back at the same spot), mirroring
        // the original's streamAddonOrderChanged() guard.
        let changed = before.length !== after.length;
        for (let i = 0; !changed && i < after.length; i++) {
            if (before[i] !== after[i]) changed = true;
        }
        if (changed && dotnet) {
            dotnet.invokeMethodAsync(method, after);
        }
    }

    function onPointerUp(e) {
        if (!drag) return;
        e.preventDefault();
        endDrag(e);
    }

    list.addEventListener('pointerdown', onPointerDown);
    list.addEventListener('pointermove', onPointerMove);
    list.addEventListener('pointerup', onPointerUp);
    list.addEventListener('pointercancel', onPointerUp);

    const dispose = function () {
        list.removeEventListener('pointerdown', onPointerDown);
        list.removeEventListener('pointermove', onPointerMove);
        list.removeEventListener('pointerup', onPointerUp);
        list.removeEventListener('pointercancel', onPointerUp);
        _addonBindings.delete(list);
    };
    _addonBindings.set(list, dispose);
}

// Stream-addon list reorder — named wrapper kept for StreamsSection's existing call.
export function bindAddonReorder(list, dotnet) {
    return bindReorder(list, dotnet, {
        rowSelector: '.stream-addon-row',
        handleSelector: '.stream-addon-drag-handle',
        dataAttr: 'data-url',
        method: 'OnAddonReorder',
    });
}

// Danger-zone uid mutations (regenerate / sign-out-everywhere / delete). POSTed so a top-level GET
// (<img>/link) can't trigger them, with the same-origin X-Requested-With proof the server's
// CsrfOrAjaxFilter accepts (a cross-origin page can't set that header without a CORS preflight these
// routes refuse). credentials:'same-origin' sends the anisync_uid cookie so the server can resolve and
// rewrite/clear it on the response; on success we reload to returnUrl so the circuit re-derives the
// credential from the (new/absent) cookie. Returns false on failure so the caller can show a status.
export async function postAuth(url, returnUrl) {
    try {
        const r = await fetch(url, {
            method: 'POST',
            headers: { 'X-Requested-With': 'XMLHttpRequest' },
            credentials: 'same-origin',
        });
        if (!r.ok) return false;
        window.location.assign(returnUrl);
        return true;
    } catch {
        return false;
    }
}
