// Helpers for ClientCache (the C# IClientCache lives in Services/ClientCache.cs).
// The hot path (get/set/remove of a single key) goes straight through the global
// localStorage.* interop and needs no module; only the prefix sweep on logout does,
// since IJSRuntime can't enumerate localStorage keys without a loop.

// Drop every localStorage key that starts with `prefix` (the cache's "anisync.cache." namespace).
// Iterate backwards because removeItem reindexes the store as we go.
export function clearPrefix(prefix) {
    try {
        for (let i = localStorage.length - 1; i >= 0; i--) {
            const k = localStorage.key(i);
            if (k && k.indexOf(prefix) === 0) localStorage.removeItem(k);
        }
    } catch (_) { /* storage blocked — nothing to clear */ }
}

// Read several cache entries in ONE interop call so ClientCache can warm its in-memory tier before a
// page's components render — without this each component's first read is a separate async round-trip
// (painful on Blazor Server, where every localStorage hit crosses SignalR). Returns { key: rawJson|null }
// keyed by the bare (unprefixed) key.
export function batchGet(prefix, keys) {
    const out = {};
    for (const k of keys) {
        try { out[k] = localStorage.getItem(prefix + k); } catch (_) { out[k] = null; }
    }
    return out;
}

