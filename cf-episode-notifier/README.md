# AniSync Episode Notifier

Cloudflare Worker that wakes a (possibly sleeping) AniSync deployment
the moment an episode airs, so episode-release notifications fire in
real time even when Fly.io's auto-stop has spun the machine down.

**Free-plan compatible** — no Durable Objects, no Queues, just cron
triggers + a tiny KV namespace. Worker burns ~1,440 cron ticks/day
(free); AniSync only wakes when an episode is actually due (~20
times/day).

## How it works

```
Daily 01:00 UTC cron  ──►  Worker fetches /api/v1/airing/upcoming?hours=48
                           Stores airing timestamps in KV
                                                            
Every minute cron     ──►  Worker reads schedule from KV
                           Any episode airing within last 90s?
                                │
                                ├── no  →  Worker exits, AniSync stays asleep
                                └── yes →  POST /api/v1/cron/check-releases
                                           AniSync wakes, dispatcher runs,
                                           notifications get inserted, machine
                                           auto-stops again
```

The 90-second look-back tolerates typical CF cron skew without missing
events. AniSync's `INSERT OR IGNORE` on the notifications unique index
means redundant pings (a flapping cron, an episode landing on a window
boundary) collapse to no-ops — over-pinging is always safe.

## Prerequisites

- A Cloudflare account on the **Workers Free plan**.
- An AniSync deployment with `ANISYNC_CRON_SECRET` set to a shared
  secret (env var or Fly secret).
- Node.js 18+ on the deploy machine (only for `wrangler`; the runtime
  is Cloudflare's).

## One-time setup

```bash
cd cf-episode-notifier
npm install
npx wrangler login          # opens a browser tab; sign in to Cloudflare
```

### 1. Create the KV namespace

```bash
npx wrangler kv:namespace create SCHEDULE
```

Wrangler prints something like:

```
✨ Success! Add the following to your configuration file:
[[kv_namespaces]]
binding = "SCHEDULE"
id = "abcd1234ef56..."
```

Copy the `id` value and paste it into `wrangler.toml` in place of the
`REPLACE_WITH_KV_NAMESPACE_ID` placeholder.

### 2. Configure the secrets

```bash
npx wrangler secret put ANISYNC_URL
# enter your AniSync base URL, e.g. https://anisync.fly.dev

npx wrangler secret put CRON_SECRET
# enter the SAME random string you set as ANISYNC_CRON_SECRET on the AniSync side
```

The matching `ANISYNC_CRON_SECRET` env var must be set on the AniSync
host:

- **Fly.io**: `fly secrets set ANISYNC_CRON_SECRET=<same string>`
- **Docker / docker-compose**: add to the `environment:` block.
- **systemd unit**: `Environment=ANISYNC_CRON_SECRET=...`

Restart AniSync so it picks up the new env var.

### 3. Deploy

```bash
npx wrangler deploy
```

Wrangler prints the deployed URL and registers both cron triggers.
Within a few minutes the daily-refresh cron seeds the KV cache; from
that point the per-minute tick decides whether to wake AniSync.

## Verifying it works

### Seed the schedule manually

```bash
curl -X POST https://anisync-episode-notifier.<your-subdomain>.workers.dev/refresh
```

Then check `wrangler tail` — you should see something like:

```
refreshSchedule: stored 47 airing times
```

### Force a tick

```bash
curl -X POST https://anisync-episode-notifier.<your-subdomain>.workers.dev/tick
```

Logs will show either:

- `tick: 0 due` (nothing in the last 90s) — nothing to do, AniSync stays asleep, OR
- `pingAniSync: N due, status=200` — Worker pinged AniSync, dispatch ran.

### Verify AniSync got the ping

Tail AniSync's logs (Fly's `fly logs` or whatever your platform uses)
and look for:

```
EpisodeNotificationScheduler armed N future timers, recovered M past episodes
```

## Cost on the free plan

| Resource | Per day | Free-plan limit | Headroom |
|---|---|---|---|
| Worker invocations (cron ticks) | 1,441 | 100,000 | ~70× |
| KV reads (schedule lookup per tick) | ~1,440 | 100,000 | ~70× |
| KV writes (daily schedule write) | 1 | 1,000 | huge |
| AniSync POSTs (real dispatches) | ~20 | (Fly's billing applies) | n/a |

## Local development

```bash
npx wrangler dev --test-scheduled
# then in another shell:
curl -X POST "http://localhost:8787/__scheduled?cron=*+*+*+*+*"  # simulate per-minute tick
curl -X POST "http://localhost:8787/__scheduled?cron=0+1+*+*+*"  # simulate daily-refresh tick
```

Point `ANISYNC_URL` at a tunnel to your local dotnet host (e.g. a
`cloudflared tunnel`), or just run `wrangler dev --remote` against a
deployed AniSync.

## Tearing it down

If you stop wanting the Worker:

```bash
npx wrangler delete
```

AniSync's in-process scheduler keeps running normally; you just lose
real-time wake-ups when the Fly machine is asleep. The 24h recovery
on next user-triggered wake still catches missed notifications.
