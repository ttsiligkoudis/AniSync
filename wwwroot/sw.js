// Service worker — minimal "shell" cache so AniSync qualifies as a PWA
// and the home screen icon launches even when the device is briefly
// offline. Deliberately conservative: we don't cache list data, addon
// payloads, or anything user-specific because stale entries are worse
// than a network error for a tracker app.
//
// Strategy:
//   - Install: precache a tiny static-asset shell (CSS, JS, icons,
//     manifest, logo) so the next launch paints a styled page even on
//     a flaky / no-connection device. Bumping CACHE_VERSION evicts the
//     old cache cleanly.
//   - Fetch: network-first for everything; only fall back to cache for
//     same-origin GETs whose response is also in the precache list.
//     ignoreSearch:true on the cache lookup so Razor's
//     asp-append-version "?v=<hash>" suffix still matches the bare
//     cache key — otherwise the offline render came back unstyled
//     because /css/site.css?v=abc never matched the cached
//     /css/site.css entry.
//     POSTs / API calls / cross-origin requests pass through verbatim.
const CACHE_VERSION = 'anisync-shell-v3';
const SHELL_ASSETS = [
    '/',
    '/css/site.css',
    '/lib/bootstrap/dist/css/bootstrap.min.css',
    '/lib/jquery/dist/jquery.min.js',
    '/js/loader.js',
    '/js/toast.js',
    '/js/pwa-install.js',
    '/js/scroll-top.js',
    '/manifest.webmanifest',
    '/logo.png',
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
            // ignoreSearch:true so versioned URLs (?v=hash from
            // asp-append-version) match the unversioned cache keys.
            // Falls through to the precached "/" shell when the
            // specific request isn't in cache (typical offline
            // navigation to an uncached route).
            return caches.match(req, { ignoreSearch: true }).then(function (cached) {
                return cached || caches.match('/', { ignoreSearch: true });
            });
        })
    );
});

// Web Push handler — fires when the upstream push provider (FCM /
// Mozilla autopush / etc.) delivers a payload from AniSync's
// PushNotificationService. Payload shape matches what the .NET side
// serialises in PushNotificationService.SendAsync.
self.addEventListener('push', function (event) {
    var data = {};
    try { data = event.data ? event.data.json() : {}; }
    catch (_) { /* keep default {} */ }

    var title = data.title || 'AniSync';
    var options = {
        body: data.body || '',
        icon: data.icon || '/icons/icon-192.png',
        badge: '/icons/icon-192.png',
        // `tag` collapses repeated pushes for the same airing into a
        // single OS notification (Chrome stacks otherwise) — the server
        // emits "anisync:<animeId>:<episode>" so a re-push from the
        // dispatcher doesn't double up.
        tag: data.tag || undefined,
        data: { url: data.url || '/notifications' },
    };
    event.waitUntil(self.registration.showNotification(title, options));
});

// Notification click: focus an existing tab on the deep-link URL if
// one's open, otherwise pop a new window. Matches the bell-dropdown's
// behaviour for in-page clicks (go straight to the watch page).
self.addEventListener('notificationclick', function (event) {
    event.notification.close();
    var url = (event.notification.data && event.notification.data.url) || '/notifications';
    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (matched) {
            for (var i = 0; i < matched.length; i++) {
                var c = matched[i];
                try {
                    var path = new URL(c.url).pathname;
                    if (path === url && 'focus' in c) return c.focus();
                } catch (_) { /* ignore malformed URLs */ }
            }
            if (self.clients.openWindow) return self.clients.openWindow(url);
        })
    );
});
