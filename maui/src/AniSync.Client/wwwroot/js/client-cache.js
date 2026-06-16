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
