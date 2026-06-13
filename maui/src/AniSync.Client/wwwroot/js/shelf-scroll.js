// Horizontal pagination for dashboard shelves.
//
// Each shelf's poster row is its own `overflow-x: auto` scroller, so "scrolled near
// the end" is detected with a plain scroll listener on the row — far more reliable for
// a nested horizontal scroller than an IntersectionObserver against the viewport, which
// scroll-snap + the shelf's vertical position make flaky (that approach only paged the
// first shelf). The Blazor component owns the fetch/append; we just tell it when the row
// is within NEAR_END px of its right edge, via OnSentinelVisibleAsync.
//
// observe() is handed the trailing SENTINEL element (a direct child of the
// .poster-scroll-row) purely as an anchor to reach the scroller — the component already
// holds that @ref, so no extra plumbing through PosterGrid is needed.

const shelves = new Map();
let nextId = 1;
const NEAR_END = 600; // px from the right edge at which we pull the next page (pre-load)

function nearEnd(row) {
    return row.scrollLeft + row.clientWidth >= row.scrollWidth - NEAR_END;
}

export function observe(sentinel, dotnet) {
    if (!sentinel || !dotnet) return 0;
    const row = sentinel.parentElement; // the .poster-scroll-row
    if (!row) return 0;

    const id = nextId++;
    // `armed` makes us fire once per approach to the end: it re-arms only after the row
    // scrolls back out of the trigger zone (or recheck() re-arms it post-append), so a
    // burst of scroll events near the end doesn't spam the interop channel.
    const entry = { row, dotnet, armed: true };
    entry.onScroll = function () {
        if (nearEnd(row)) {
            if (entry.armed) { entry.armed = false; dotnet.invokeMethodAsync('OnSentinelVisibleAsync'); }
        } else {
            entry.armed = true;
        }
    };
    row.addEventListener('scroll', entry.onScroll, { passive: true });
    shelves.set(id, entry);

    // If the first page doesn't even fill the row there's nothing to scroll — pull more.
    recheck(id);
    return id;
}

// Called by the component after it appends a page: a frame later (so the new cards are
// laid out), if the row is still scrolled near its end — a short page, or one that didn't
// push past the threshold — pull again; otherwise re-arm for the next scroll-to-end.
export function recheck(id) {
    const e = shelves.get(id);
    if (!e) return;
    requestAnimationFrame(function () {
        if (!shelves.has(id)) return;
        if (nearEnd(e.row)) { e.armed = false; e.dotnet.invokeMethodAsync('OnSentinelVisibleAsync'); }
        else { e.armed = true; }
    });
}

export function disconnect(id) {
    const e = shelves.get(id);
    if (e) { e.row.removeEventListener('scroll', e.onScroll); shelves.delete(id); }
}
