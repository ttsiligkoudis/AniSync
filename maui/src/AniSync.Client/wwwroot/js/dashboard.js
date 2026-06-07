// Dashboard body-class toggle. The Blazor Home page sets `dash-page` on <body> on its
// first interactive render and clears it on dispose — the equivalent of the original
// Index.cshtml's `ViewData["BodyClass"] = "dash-page"`. It drives `body.dash-page main`,
// which makes <main> the containing block for the absolutely-positioned "Customize"
// float (so the button lands on the media-type pills row instead of anchoring to the
// viewport) and tightens the top padding so the content tucks under the sticky pills.
export function setDashPage(on) {
    document.body.classList.toggle('dash-page', !!on);
}

// Anonymous dashboard layout (the Customize modal's reorder/hide). Signed-in users persist to their
// account instead; this localStorage copy lets a logged-out visitor's customisation survive a reload.
const LAYOUT_KEY = 'anisync.dashLayout';
export function getLayout() {
    try { return localStorage.getItem(LAYOUT_KEY); } catch (_) { return null; }
}
export function setLayout(json) {
    try { localStorage.setItem(LAYOUT_KEY, json); } catch (_) { /* private mode */ }
}
