// Infinite-scroll bridge for the Discover / browse-by poster grids. Mirrors the web
// app's discover-pagination.js, but here the JS only owns the IntersectionObserver —
// the data fetch + grid render stay in Blazor. When the sentinel scrolls within 400px
// of the viewport we invoke OnSentinelVisibleAsync on the component, which fetches +
// appends the next page and then calls reobserve() so a short page that didn't push
// the sentinel out of view keeps pulling until the viewport fills or the catalog ends.
//
// observe() returns an integer handle the component holds for reobserve()/disconnect()
// — simpler than round-tripping the ElementReference back through interop each time.
const observers = new Map();
let nextId = 1;

export function observe(sentinel, dotnet) {
    if (!sentinel || !dotnet) return 0;
    // rootMargin 400px → fetch the next page before the sentinel actually enters the
    // viewport so the user rarely sees a stall (same trade-off as discover-pagination.js).
    const observer = new IntersectionObserver(function (entries) {
        for (let i = 0; i < entries.length; i++) {
            if (entries[i].isIntersecting) { dotnet.invokeMethodAsync('OnSentinelVisibleAsync'); return; }
        }
    }, { rootMargin: '400px' });
    observer.observe(sentinel);
    const id = nextId++;
    observers.set(id, { observer, sentinel });
    return id;
}

// Re-arm after an append: an IntersectionObserver only fires on *transitions*, so a
// short page (e.g. a 20-item provider cap) that left the sentinel still in view would
// never fire again and the grid would stall. unobserve+observe forces a fresh
// intersection check on the next frame — a no-op if it has already scrolled past.
export function reobserve(id) {
    const o = observers.get(id);
    if (o && o.sentinel) { o.observer.unobserve(o.sentinel); o.observer.observe(o.sentinel); }
}

export function disconnect(id) {
    const o = observers.get(id);
    if (o) { o.observer.disconnect(); observers.delete(id); }
}
