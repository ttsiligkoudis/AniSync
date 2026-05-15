// AniSync episode notifier — Cloudflare Worker cron.
//
// Every 5 minutes (per wrangler.toml [triggers]) POST to AniSync's
// /api/v1/cron/check-releases endpoint with a shared-secret header.
// The .NET side handles the actual work: pulling the AniList airing
// schedule, refreshing stale per-user "Watching" caches, and creating
// notification rows for each matched user.
//
// Required secrets:
//   ANISYNC_URL — base URL of the AniSync deployment (e.g. https://anisync.fly.dev)
//   CRON_SECRET — must match ANISYNC_CRON_SECRET on the .NET side
//
// This worker is intentionally dumb: it has no idea what anime exists
// or who's subscribed. It only exists because Fly.io ASP.NET hosts
// don't have a built-in cron runner and we'd rather not wire an
// IHostedService just for the timer.

async function triggerCheck(env) {
    if (!env.ANISYNC_URL) throw new Error('ANISYNC_URL is not configured');
    if (!env.CRON_SECRET) throw new Error('CRON_SECRET is not configured');

    const base = env.ANISYNC_URL.replace(/\/$/, '');
    const url = `${base}/api/v1/cron/check-releases`;
    const started = Date.now();

    const res = await fetch(url, {
        method: 'POST',
        headers: {
            'X-Cron-Secret': env.CRON_SECRET,
            'Content-Type': 'application/json',
            'User-Agent': 'anisync-episode-notifier/1.0',
        },
        body: '{}',
    });

    const elapsed = Date.now() - started;
    let body = null;
    try { body = await res.text(); } catch (_) { /* ignore */ }
    return { status: res.status, ok: res.ok, elapsedMs: elapsed, body };
}

export default {
    async scheduled(event, env, ctx) {
        // waitUntil keeps the worker alive past the synchronous return
        // so the fetch + log complete before the runtime tears down.
        ctx.waitUntil((async () => {
            try {
                const r = await triggerCheck(env);
                console.log(`[notifier] cron tick → ${r.status} in ${r.elapsedMs}ms${r.ok ? '' : ` body=${(r.body || '').slice(0, 200)}`}`);
            } catch (e) {
                console.error('[notifier] cron tick failed:', e && e.message ? e.message : String(e));
            }
        })());
    },

    // Manual trigger surface — useful for local testing (`wrangler dev` +
    // `curl -X POST http://localhost:8787`) and for one-off ops use. Not
    // a security boundary on its own; the .NET side is the gate.
    async fetch(request, env) {
        if (request.method !== 'POST') {
            return new Response(
                'POST to trigger the dispatcher, or wait for the */5 cron tick.',
                { status: 405, headers: { 'Content-Type': 'text/plain' } }
            );
        }
        try {
            const r = await triggerCheck(env);
            return new Response(JSON.stringify(r), {
                status: r.ok ? 200 : 502,
                headers: { 'Content-Type': 'application/json' },
            });
        } catch (e) {
            return new Response(
                JSON.stringify({ ok: false, error: e && e.message ? e.message : String(e) }),
                { status: 500, headers: { 'Content-Type': 'application/json' } }
            );
        }
    },
};
