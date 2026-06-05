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

// ── Stream-addon drag reorder ─────────────────────────────────────────────
// Pointer-event reorder on the drag handle — a faithful port of the
// _ConfigurePageScript handler. Single code path for mouse + touch + pen
// (setPointerCapture redirects move/up to the handle even when the pointer
// drifts off it). On drop it reads the new row order and hands it back to .NET
// via dotnet.invokeMethodAsync('OnAddonReorder', urls) so the component can
// persist it through the same Api.ReorderStreamAddonsAsync call the page uses.
const _addonBindings = new WeakMap();

export function bindAddonReorder(list, dotnet) {
    if (!list) return;
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
        const rows = list.querySelectorAll('.stream-addon-row:not(.is-dragging)');
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
        return Array.from(list.querySelectorAll('.stream-addon-row'))
            .map(function (r) { return r.getAttribute('data-url') || ''; })
            .filter(function (u) { return !!u; });
    }

    function onPointerDown(e) {
        const handle = e.target.closest('.stream-addon-drag-handle');
        if (!handle || !list.contains(handle)) return;
        // Ignore right-click / middle-click — e.button is 0 for left/touch/pen.
        if (e.button !== undefined && e.button !== 0) return;
        const row = handle.closest('.stream-addon-row');
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
        const drop = (e && typeof e.clientY === 'number') ? findDropTarget(e.clientY) : null;
        if (drop) {
            if (drop.before) list.insertBefore(row, drop.row);
            else list.insertBefore(row, drop.row.nextSibling);
        }
        row.classList.remove('is-dragging');
        clearIndicators();
        try { handle.releasePointerCapture(drag.pointerId); } catch (_) {}
        drag = null;

        // Only notify .NET when the order actually changed (a drag that drops
        // back at the original spot is a no-op, mirroring the original's
        // streamAddonOrderChanged() guard).
        const after = currentOrder();
        let changed = before.length !== after.length;
        for (let i = 0; !changed && i < after.length; i++) {
            if (before[i] !== after[i]) changed = true;
        }
        if (changed && dotnet) {
            dotnet.invokeMethodAsync('OnAddonReorder', after);
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
