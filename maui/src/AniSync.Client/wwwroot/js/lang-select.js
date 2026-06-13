// Sets a native <select>'s value from .NET after render. Blazor's @bind/value handling on a
// <select> doesn't reliably select the matching <option> when the value is loaded asynchronously
// (and across the prerender→interactive handover), leaving the control blank. Setting el.value
// directly — once the options exist — picks the right option every time.
export function setValue(el, value) {
    if (el && value != null && el.value !== value) {
        el.value = value;
    }
}
