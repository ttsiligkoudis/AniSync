// Browser Web Push subscribe / unsubscribe flow.
//
// Drives the "Enable browser notifications" toggle on the /notifications
// page. Hides itself entirely when the browser doesn't support push
// (`PushManager` / `Notification` missing — Safari before iOS 16.4, some
// in-app webviews) or when the deployment hasn't configured VAPID keys
// (server returns `{enabled: false}` from /api/v1/push/vapid-key).
//
// Flow:
//   1. On load: check support + server status + current subscription
//   2. Render: hidden | "Enable" | "Enabled ✓ · Disable" | "Permission denied"
//   3. Enable click: request permission (if needed) → PushManager.subscribe
//      → POST to /api/v1/push/subscribe
//   4. Disable click: PushManager.getSubscription().unsubscribe() → POST
//      to /api/v1/push/unsubscribe
(function () {
    'use strict';

    var toggle = document.querySelector('[data-push-toggle]');
    if (!toggle) return;

    var label = toggle.querySelector('[data-push-toggle-label]');
    var btn = toggle.querySelector('[data-push-toggle-btn]');

    function setHidden() {
        toggle.hidden = true;
    }

    function render(state) {
        // state: 'enable' | 'disable' | 'denied' | 'unsupported'
        toggle.hidden = false;
        toggle.classList.remove(
            'notif-push-toggle-enable',
            'notif-push-toggle-disable',
            'notif-push-toggle-denied',
            'notif-push-toggle-unsupported',
        );
        toggle.classList.add('notif-push-toggle-' + state);
        toggle.dataset.state = state;
        if (state === 'enable') {
            label.textContent = 'Get notified when episodes drop';
            btn.textContent = 'Enable browser notifications';
            btn.disabled = false;
            btn.hidden = false;
        } else if (state === 'disable') {
            label.textContent = '✓ Browser notifications enabled';
            btn.textContent = 'Disable';
            btn.disabled = false;
            btn.hidden = false;
        } else if (state === 'denied') {
            label.textContent = 'Notifications blocked in your browser settings.';
            btn.hidden = true;
        } else {
            // unsupported
            setHidden();
        }
    }

    if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
        render('unsupported');
        return;
    }

    // Helper: convert base64url-encoded VAPID key to the Uint8Array
    // PushManager.subscribe expects as applicationServerKey.
    function urlBase64ToUint8Array(base64String) {
        var padding = '='.repeat((4 - (base64String.length % 4)) % 4);
        var base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
        var rawData = atob(base64);
        var out = new Uint8Array(rawData.length);
        for (var i = 0; i < rawData.length; i++) out[i] = rawData.charCodeAt(i);
        return out;
    }

    var serverPublicKey = null;

    async function loadInitial() {
        // Server might not have VAPID configured (admins haven't set
        // Push:VapidPublicKey). Hide the toggle entirely in that case.
        try {
            var res = await fetch('/api/v1/push/vapid-key', {
                credentials: 'same-origin',
                skipLoader: true,
            });
            if (!res.ok) { setHidden(); return; }
            var data = await res.json();
            if (!data || !data.enabled || !data.publicKey) { setHidden(); return; }
            serverPublicKey = data.publicKey;
        } catch (_) {
            setHidden();
            return;
        }

        if (Notification.permission === 'denied') {
            render('denied');
            return;
        }

        try {
            var registration = await navigator.serviceWorker.ready;
            var existing = await registration.pushManager.getSubscription();
            render(existing ? 'disable' : 'enable');
        } catch (_) {
            // SW not ready / PushManager throwing — fall back to enable
            // and let the click handler surface a clearer error.
            render('enable');
        }
    }

    async function enable() {
        btn.disabled = true;
        try {
            var permission = Notification.permission === 'granted'
                ? 'granted'
                : await Notification.requestPermission();
            if (permission !== 'granted') {
                render('denied');
                return;
            }

            var registration = await navigator.serviceWorker.ready;
            var subscription = await registration.pushManager.getSubscription();
            if (!subscription) {
                subscription = await registration.pushManager.subscribe({
                    userVisibleOnly: true,
                    applicationServerKey: urlBase64ToUint8Array(serverPublicKey),
                });
            }

            // Browser returns base64url-encoded p256dh + auth on the
            // PushSubscription's .toJSON() output. The .NET side expects
            // the same shape.
            var json = subscription.toJSON();
            var res = await fetch('/api/v1/push/subscribe', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    endpoint: json.endpoint,
                    keys: { p256dh: json.keys && json.keys.p256dh, auth: json.keys && json.keys.auth },
                }),
                skipLoader: true,
            });
            if (!res.ok) {
                // Best-effort rollback so we don't leave a subscription
                // the server doesn't know about.
                try { await subscription.unsubscribe(); } catch (_) { /* ignore */ }
                render('enable');
                return;
            }
            render('disable');
        } catch (_) {
            render('enable');
        } finally {
            btn.disabled = false;
        }
    }

    async function disable() {
        btn.disabled = true;
        try {
            var registration = await navigator.serviceWorker.ready;
            var subscription = await registration.pushManager.getSubscription();
            if (subscription) {
                var endpoint = subscription.endpoint;
                try { await subscription.unsubscribe(); } catch (_) { /* ignore */ }
                try {
                    await fetch('/api/v1/push/unsubscribe', {
                        method: 'POST',
                        credentials: 'same-origin',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ endpoint: endpoint }),
                        skipLoader: true,
                    });
                } catch (_) { /* server-side cleanup is best-effort */ }
            }
            render('enable');
        } finally {
            btn.disabled = false;
        }
    }

    btn.addEventListener('click', function () {
        if (toggle.dataset.state === 'disable') disable();
        else enable();
    });

    loadInitial();
})();
