// Airing-time localization for the shared poster grid — exact port of the inline
// <script> in Views/Shared/_PosterGrid.cshtml (the web app's source of truth).
//
// For each card whose progress badge carries a `data-airing-at` unix-seconds
// timestamp, convert it to the viewer's LOCAL "Ep N · HH:MM" and reorder the
// cards within each affected grid by local clock-of-day. The server sorts by
// UTC airingAt ascending, but for viewers in positive-UTC timezones a late-UTC
// airing wraps past local midnight into the next morning and reads as an earlier
// clock time than the late-evening entries above it; sorting after conversion
// keeps the badges in ascending clock order regardless of where the UTC date
// boundary falls.
//
// `data-airing-at` is stripped after formatting so a re-render (or a sibling
// grid's pass) skips the already-handled cards — mirrors the web app, where the
// partial is emitted multiple times per page.
export function localizeAiring(root) {
    var scope = root || document;
    var affectedGrids = new Set();
    scope.querySelectorAll('.library-card-progress[data-airing-at]').forEach(function (el) {
        var ts = parseInt(el.getAttribute('data-airing-at'), 10);
        el.removeAttribute('data-airing-at');
        if (!ts) return;
        var d = new Date(ts * 1000);
        var hh = String(d.getHours()).padStart(2, '0');
        var mm = String(d.getMinutes()).padStart(2, '0');
        el.textContent = el.textContent + ' · ' + hh + ':' + mm;

        // Record the local clock-of-day on the parent card for the reorder
        // step. Minutes-since-midnight is the natural sort key for "ascending
        // clock time" semantics.
        var card = el.closest('.library-card');
        if (!card) return;
        card.dataset.airingLocalKey = d.getHours() * 60 + d.getMinutes();
        if (card.parentElement) affectedGrids.add(card.parentElement);
    });

    affectedGrids.forEach(function (grid) {
        var cards = Array.prototype.slice
            .call(grid.children)
            .filter(function (c) { return c.dataset && c.dataset.airingLocalKey != null; });
        cards.sort(function (a, b) {
            return parseInt(a.dataset.airingLocalKey, 10) - parseInt(b.dataset.airingLocalKey, 10);
        });
        cards.forEach(function (card) {
            delete card.dataset.airingLocalKey;
            grid.appendChild(card);
        });
    });
}
