// Sets a native <select>'s value from .NET after render. Blazor's @bind/value handling on a
// <select> doesn't reliably select the matching <option> when the value is loaded asynchronously
// (and across the prerender→interactive handover), leaving the control blank. Setting el.value
// directly — once the options exist — picks the right option every time.
export function setValue(el, value) {
    if (!el || value == null) return;
    el.value = value;
    // Re-assert via each <option>'s `selected` flag. The value is pinned once on first render while the
    // Streams modal is still hidden (display:none); some WebViews then fail to repaint the closed-select
    // label when it becomes visible, leaving it blank. Forcing option.selected updates the shown label.
    // (Dropped the `el.value !== value` guard for the same reason — a re-pin must not no-op.)
    for (let i = 0; i < el.options.length; i++) {
        el.options[i].selected = (el.options[i].value === value);
    }
}
