// AniSync episode notifier — Cloudflare Worker (free-plan compatible).
//
// Two cron triggers:
//   - 0 1 * * *  →  daily refresh. Fetches /api/v1/airing/upcoming?hours=48
//                   from AniSync and stores the airing timestamps in KV.
//   - * * * * *  →  per-minute tick. Reads the cached schedule from KV
//                   and POSTs /api/v1/cron/check-releases on AniSync if
//                   any episode airs within the last ~90s window.
//
// The Worker burns ~1,440 cron ticks/day (free) but AniSync only wakes
// when an episode is actually due (~20 times/day). The dispatcher's
// own 24h recovery + the notifications table's UNIQUE INDEX make the
// ping idempotent — if multiple ticks fire within the same airing
// window, duplicate work collapses to no-ops via INSERT OR IGNORE.

const SCHEDULE_KEY = 'schedule';
const DAILY_CRON = '0 1 * * *';

// 90 seconds back-look catches typical Cloudflare cron skew without
// missing events. AniSync handles redundant pings via its idempotent
// dispatch, so erring on the side of slightly-wider is safe.
const DUE_WINDOW_SEC = 90;

async function refreshSchedule(env) {
    if (!env.ANISYNC_URL) {
        console.error('ANISYNC_URL is not configured');
        return;
    }
    const base = env.ANISYNC_URL.replace(/\/$/, '');
    try {
        const res = await fetch(`${base}/api/v1/airing/upcoming?hours=48`, {
            headers: { 'User-Agent': 'anisync-episode-notifier/1.0' },
        });
        if (!res.ok) {
            console.error(`refreshSchedule: HTTP ${res.status}`);
            return;
        }
        const json = await res.json();
        // We only need airing timestamps — strip everything else so the
        // KV payload stays small (KV per-value max is 25 MiB, but
        // smaller = faster read).
        const times = (json && Array.isArray(json.items) ? json.items : [])
            .map((it) => Number(it.airingAt))
            .filter((t) => Number.isFinite(t) && t > 0)
            .sort((a, b) => a - b);
        await env.SCHEDULE.put(SCHEDULE_KEY, JSON.stringify(times));
        console.log(`refreshSchedule: stored ${times.length} airing times`);
    } catch (e) {
        console.error(`refreshSchedule failed: ${e && e.message ? e.message : e}`);
    }
}

async function pingAniSync(env, dueCount) {
    if (!env.ANISYNC_URL || !env.CRON_SECRET) {
        console.error('ANISYNC_URL or CRON_SECRET not configured');
        return;
    }
    const base = env.ANISYNC_URL.replace(/\/$/, '');
    const started = Date.now();
    try {
        const res = await fetch(`${base}/api/v1/cron/check-releases`, {
            method: 'POST',
            headers: {
                'X-Cron-Secret': env.CRON_SECRET,
                'Content-Type': 'application/json',
                'User-Agent': 'anisync-episode-notifier/1.0',
            },
            body: '{}',
        });
        const elapsed = Date.now() - started;
        console.log(`pingAniSync: ${dueCount} due, status=${res.status} elapsed=${elapsed}ms`);
    } catch (e) {
        console.error(`pingAniSync failed: ${e && e.message ? e.message : e}`);
    }
}

async function tick(env) {
    const raw = await env.SCHEDULE.get(SCHEDULE_KEY);
    if (!raw) {
        // No schedule cached yet — the daily-refresh tick hasn't run
        // since deploy / KV wipe. Trigger one now so the next tick has
        // data to act on, then bail this tick.
        console.log('tick: schedule not cached yet, seeding via refresh');
        await refreshSchedule(env);
        return;
    }
    let times;
    try {
        times = JSON.parse(raw);
    } catch {
        console.error('tick: schedule JSON parse failed, re-seeding');
        await refreshSchedule(env);
        return;
    }
    if (!Array.isArray(times) || times.length === 0) return;

    const nowSec = Math.floor(Date.now() / 1000);
    const cutoff = nowSec - DUE_WINDOW_SEC;
    const dueCount = times.reduce(
        (acc, t) => (t >= cutoff && t <= nowSec ? acc + 1 : acc),
        0,
    );
    if (dueCount === 0) return;

    await pingAniSync(env, dueCount);
}

export default {
    async scheduled(event, env, ctx) {
        ctx.waitUntil(
            (async () => {
                try {
                    if (event.cron === DAILY_CRON) {
                        await refreshSchedule(env);
                    } else {
                        await tick(env);
                    }
                } catch (e) {
                    console.error(`scheduled handler failed: ${e && e.message ? e.message : e}`);
                }
            })(),
        );
    },

    // Manual trigger surface for testing + ops. `wrangler dev` exposes
    // this on localhost so a developer can curl /refresh or /tick to
    // verify behaviour without waiting for the cron.
    async fetch(request, env) {
        if (request.method !== 'POST') {
            return new Response(
                'POST /refresh to re-pull the schedule\nPOST /tick to evaluate due episodes now',
                { status: 405, headers: { 'Content-Type': 'text/plain' } },
            );
        }
        const url = new URL(request.url);
        try {
            if (url.pathname === '/refresh') {
                await refreshSchedule(env);
                return new Response('refreshed\n');
            }
            if (url.pathname === '/tick') {
                await tick(env);
                return new Response('ticked\n');
            }
            return new Response('not found', { status: 404 });
        } catch (e) {
            return new Response(
                JSON.stringify({ ok: false, error: e && e.message ? e.message : String(e) }),
                { status: 500, headers: { 'Content-Type': 'application/json' } },
            );
        }
    },
};
