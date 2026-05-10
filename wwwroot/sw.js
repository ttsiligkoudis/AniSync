// Service worker — minimal "shell" cache so AniSync qualifies as a PWA
// and the home screen icon launches even when the device is briefly
// offline. Deliberately conservative: we don't cache list data, addon
// payloads, or anything user-specific because stale entries are worse
// than a network error for a tracker app.
//
// Strategy:
//   - Install: precache a tiny static-asset shell (CSS, JS, icons,
//     manifest) so the next launch paints something even on a flaky
//     connection. Bumping CACHE_VERSION evicts the old cache cleanly.
//   - Fetch: network-first for everything; only fall back to cache for
//     same-origin GETs whose response is also in the precache list.
//     POSTs / API calls / cross-origin requests pass through verbatim.
const CACHE_VERSION = 'anisync-shell-v1';
const SHELL_ASSETS = [
    '/',
    '/css/site.css',
    '/manifest.webmanifest',
    '/icons/icon-192.png',
    '/icons/icon-512.png',
];

self.addEventListener('install', function (event) {
    event.waitUntil(
        caches.open(CACHE_VERSION).then(function (cache) {
            return cache.addAll(SHELL_ASSETS).catch(function () {
                // Some shell URLs may 404 in dev — best-effort, don't
                // fail the install so the SW still activates.
            });
        })
    );
    self.skipWaiting();
});

self.addEventListener('activate', function (event) {
    event.waitUntil(
        caches.keys().then(function (keys) {
            return Promise.all(keys.map(function (k) {
                if (k !== CACHE_VERSION) return caches.delete(k);
            }));
        }).then(function () { return self.clients.claim(); })
    );
});

self.addEventListener('fetch', function (event) {
    var req = event.request;
    if (req.method !== 'GET') return;
    var url = new URL(req.url);
    if (url.origin !== self.location.origin) return;
    // Never cache API or auth routes — those carry user state.
    if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/auth/')) return;

    event.respondWith(
        fetch(req).catch(function () {
            return caches.match(req).then(function (cached) {
                return cached || caches.match('/');
            });
        })
    );
});
