// Web-head push-subscription interop. The Notifications page calls these to opt
// in/out of browser Web Push; the C# side persists the subscription against the
// user's uid via /api/v1/push. No-ops gracefully where the browser lacks the
// Push API (the page hides the toggle when the server reports push disabled).
window.anisyncPush = (function () {
    function urlBase64ToUint8Array(base64String) {
        const padding = '='.repeat((4 - base64String.length % 4) % 4);
        const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
        const raw = atob(base64);
        const out = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
        return out;
    }

    async function registration() {
        if (!('serviceWorker' in navigator)) return null;
        try { return await navigator.serviceWorker.ready; } catch (e) { return null; }
    }

    function supported() {
        return ('serviceWorker' in navigator) && ('PushManager' in window) && ('Notification' in window);
    }

    // Requests permission + subscribes; returns { endpoint, keys:{p256dh,auth} }
    // shaped for /api/v1/push/subscribe, or null if unsupported/denied.
    async function subscribe(publicKey) {
        if (!supported()) return null;
        const perm = await Notification.requestPermission();
        if (perm !== 'granted') return null;
        const reg = await registration();
        if (!reg) return null;
        let sub = await reg.pushManager.getSubscription();
        if (!sub) {
            sub = await reg.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: urlBase64ToUint8Array(publicKey),
            });
        }
        const json = sub.toJSON();
        return { endpoint: json.endpoint, keys: { p256dh: json.keys.p256dh, auth: json.keys.auth } };
    }

    // Unsubscribes the browser; returns the removed endpoint (for the server to
    // drop), or null when there was nothing subscribed.
    async function unsubscribe() {
        const reg = await registration();
        if (!reg) return null;
        const sub = await reg.pushManager.getSubscription();
        if (!sub) return null;
        const endpoint = sub.endpoint;
        try { await sub.unsubscribe(); } catch (e) { /* already gone */ }
        return endpoint;
    }

    function permission() {
        return ('Notification' in window) ? Notification.permission : 'unsupported';
    }

    return { supported: supported, subscribe: subscribe, unsubscribe: unsubscribe, permission: permission };
})();
