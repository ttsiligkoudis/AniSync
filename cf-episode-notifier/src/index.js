// AniSync episode notifier — Cloudflare Worker (free-plan compatible).
//
// Two cron triggers:
//   - 0 1 * * *  →  daily refresh. Fetches /api/v1/airing/upcoming?hours=48
//                   from AniSync and stores the airing timestamps in KV.
//   - * * * * *  →  per-minute tick. Reads the cached schedule from KV
//                   and POSTs /api/v1/cron/check-releases on AniSync if
//                   any episode airs within the last ~90s window AND
//                   that airing hasn't already been pinged-for in a
//                   previous tick.
//
// Worker side: burns ~1,440 cron ticks/day (free), ~1,500 KV reads/day,
// ~20 KV writes/day — all well under free-plan ceilings.
// AniSync side: woken ~once per actual airing (~20 times/day), with
// the per-airing dedup eliminating the ~5–10 boundary-overlap double-
// wakes the previous design produced.

const SCHEDULE_KEY = 'schedule';
const DAILY_CRON = '0 1 * * *';

// 90 seconds back-look catches typical Cloudflare cron skew without
// missing events. AniSync's schedule_entries.notified_at flag + the
// per-airing KV dedup below mean even repeated catches collapse to
// zero AniSync wakes after the first.
const DUE_WINDOW_SEC = 90;

// 24h TTL on per-airing pinged-marker keys. Long enough to outlast
// the schedule's 1h past-lookback (an airing can't keep landing in
// the 90s due window past ~T+90s anyway), short enough to keep the
// KV namespace tidy without manual pruning.
const PINGED_KEY_TTL_SEC = 86400;

function pingedKey(airingAt) {
    return `pinged:${airingAt}`;
}

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
        return false;
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
        return res.ok;
    } catch (e) {
        console.error(`pingAniSync failed: ${e && e.message ? e.message : e}`);
        return false;
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
    const due = times.filter((t) => t >= cutoff && t <= nowSec);
    if (due.length === 0) return;

    // Filter out airings we've already pinged for in a previous tick.
    // Each ping sets a `pinged:<airingAt>` KV key with a 24h TTL so
    // the 90s-window double-catch (a boundary airing landing in two
    // consecutive ticks' windows) doesn't produce two wake calls to
    // AniSync. Reads are cheap on the free plan (~100K/day budget,
    // we use a couple per tick when something's actually due).
    const newlyDue = [];
    for (const t of due) {
        try {
            const marker = await env.SCHEDULE.get(pingedKey(t));
            if (!marker) newlyDue.push(t);
        } catch (e) {
            // KV read failure → assume not pinged (better to double-
            // ping than miss; AniSync's schedule_entries.notified_at
            // dedupes the work on its end either way).
            console.error(`tick: pinged-marker read for ${t} failed: ${e && e.message ? e.message : e}`);
            newlyDue.push(t);
        }
    }
    if (newlyDue.length === 0) {
        // Everything in the due window has already been pinged for —
        // the most common case during the 30s overlap between two
        // consecutive ticks.
        return;
    }

    const ok = await pingAniSync(env, newlyDue.length);
    // Mark pinged regardless of ok — a 5xx from AniSync means the
    // request reached the machine (which is the whole point of the
    // ping). If the request never landed at all, the next tick will
    // see this airing still in the due window and retry.
    if (!ok) {
        console.log(`tick: AniSync ping failed but airings will retry on the next tick`);
        return;
    }
    for (const t of newlyDue) {
        try {
            await env.SCHEDULE.put(pingedKey(t), '1', {
                expirationTtl: PINGED_KEY_TTL_SEC,
            });
        } catch (e) {
            console.error(`tick: mark-pinged for ${t} failed: ${e && e.message ? e.message : e}`);
        }
    }
}

// We deliberately swallow every error path back to a 2xx response or a
// silently-completed scheduled run. Cloudflare aggressively throttles
// Workers that throw repeatedly from `scheduled` handlers — a transient
// AniSync outage shouldn't disable the cron trigger that's the whole
// point of this Worker.
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
                    // Log and swallow — scheduled handlers that throw
                    // accumulate failure points against the worker's
                    // health, and CF can disable the cron after too many.
                    console.error(`scheduled handler failed: ${e && e.message ? e.message : e}`);
                }
            })(),
        );
        // No return value needed from scheduled, but explicit success
        // documents the contract for future readers.
    },

    // Manual trigger surface for testing + ops. `wrangler dev` exposes
    // this on localhost so a developer can curl /refresh or /tick to
    // verify behaviour without waiting for the cron. Always returns a
    // 2xx response — error details land in the body but the HTTP
    // status stays successful so CF doesn't count this as a failure.
    async fetch(request, env) {
        try {
            if (request.method !== 'POST') {
                return new Response(
                    'POST /refresh to re-pull the schedule\nPOST /tick to evaluate due episodes now',
                    { status: 200, headers: { 'Content-Type': 'text/plain' } },
                );
            }
            const url = new URL(request.url);
            if (url.pathname === '/refresh') {
                await refreshSchedule(env);
                return new Response('refreshed\n', { status: 200 });
            }
            if (url.pathname === '/tick') {
                await tick(env);
                return new Response('ticked\n', { status: 200 });
            }
            return new Response(
                'Try POST /refresh or POST /tick\n',
                { status: 200, headers: { 'Content-Type': 'text/plain' } },
            );
        } catch (e) {
            console.error(`fetch handler failed: ${e && e.message ? e.message : e}`);
            return new Response(
                JSON.stringify({ ok: false, error: e && e.message ? e.message : String(e) }),
                { status: 200, headers: { 'Content-Type': 'application/json' } },
            );
        }
    },
};
