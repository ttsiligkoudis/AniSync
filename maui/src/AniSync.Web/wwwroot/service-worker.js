// Conservative PWA service worker for the AniSync Web head. Deliberately does
// NOT precache Blazor's framework/app assets (their hashed names rotate every
// deploy, and stale caches would break the SPA). It only:
//   - caches the offline fallback + icons at install, and
//   - serves the offline page for navigations when the network is unreachable.
// Everything else is network pass-through, so online users always get fresh
// content and a new deploy is picked up immediately.
const CACHE = 'anisync-shell-v1';
const OFFLINE_URL = '/offline.html';
const PRECACHE = [OFFLINE_URL, '/manifest.webmanifest', '/icons/icon-192.png', '/icons/icon-512.png'];

self.addEventListener('install', (event) => {
    event.waitUntil(caches.open(CACHE).then((c) => c.addAll(PRECACHE)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', (event) => {
    const req = event.request;
    // Only handle top-level navigations; let everything else hit the network
    // untouched (no stale framework assets, no interference with SignalR).
    if (req.mode !== 'navigate') return;
    event.respondWith(
        fetch(req).catch(() => caches.match(OFFLINE_URL))
    );
});

// Web Push: the dispatcher sends {title, body, url} for new-episode notifications.
self.addEventListener('push', (event) => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch (e) { /* non-JSON payload */ }
    const title = data.title || 'AniSync';
    const options = {
        body: data.body || '',
        icon: '/icons/icon-192.png',
        badge: '/icons/icon-192.png',
        data: { url: data.url || '/notifications' },
    };
    event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    const url = (event.notification.data && event.notification.data.url) || '/notifications';
    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((cl) => {
            for (const c of cl) {
                if ('focus' in c) { c.navigate(url); return c.focus(); }
            }
            return self.clients.openWindow(url);
        })
    );
});
