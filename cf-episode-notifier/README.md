# AniSync Episode Notifier

Cloudflare Worker that cron-triggers AniSync's per-user episode-release
notification dispatcher every 5 minutes. The worker itself is dumb — it
sends an authenticated `POST /api/v1/cron/check-releases` and logs the
result. All the actual work (pulling the AniList airing schedule,
refreshing per-user "Watching" caches, creating notification rows) runs
on the .NET side.

This worker exists because Fly.io ASP.NET hosts don't have a built-in
cron runner. The alternative would be an in-process `IHostedService`
in AniSync, but that ties the timer to the app server's uptime and
instance count.

## Prerequisites

- A free Cloudflare account
- Node.js 18+ (only on the deploy machine — runtime is Cloudflare's)
- AniSync already deployed somewhere reachable from the worker

## One-time setup

```bash
cd cf-episode-notifier
npm install
npx wrangler login           # opens a browser tab; sign in to Cloudflare
```

### Configure the two secrets

```bash
npx wrangler secret put ANISYNC_URL
# enter your AniSync base URL, e.g. https://anisync.fly.dev

npx wrangler secret put CRON_SECRET
# enter a random string (32+ bytes is plenty)
```

The matching `ANISYNC_CRON_SECRET` env var must be set on the AniSync
host to the **same** value:

- **Fly.io**:
  ```bash
  fly secrets set ANISYNC_CRON_SECRET=<same string>
  ```
- **Docker / docker-compose**: add to the `environment:` block.
- **systemd unit**: add `Environment=ANISYNC_CRON_SECRET=...`.
- **Local dev (`dotnet run`)**: export it in your shell first.

Restart AniSync so it picks up the new env var.

### Deploy

```bash
npx wrangler deploy
```

Wrangler prints the deployed URL and registers the cron trigger. From
that point the worker fires `POST /api/v1/cron/check-releases` every
5 minutes; logged-in users see notifications appear in the bell as
new episodes air.

## Verifying it works

1. Sign in to AniSync with a tracker (AniList / MAL / Kitsu) that has at
   least one currently-airing show in "Watching" status.
2. Wait up to 5 minutes for the next cron tick (or manually trigger —
   see below).
3. The site-header bell should show an unread badge; click it to see
   recent episode notifications. Click a row to deep-link to the
   episode's watch page.

### Manually trigger the dispatcher

For testing, the worker also accepts `POST` to its public URL (the
.NET side is still the auth boundary — the shared secret comes from
the env var, not from the caller):

```bash
curl -X POST https://anisync-episode-notifier.<your-subdomain>.workers.dev
```

You can also bypass the worker entirely and hit AniSync directly with
the secret you configured:

```bash
curl -X POST https://anisync.<your-host>/api/v1/cron/check-releases \
     -H "X-Cron-Secret: <same string as CRON_SECRET / ANISYNC_CRON_SECRET>" \
     -H "Content-Type: application/json"
```

Both return a JSON summary:

```json
{
  "cachesRefreshed": 12,
  "cachesFailed": 0,
  "airingChecked": 47,
  "notificationsCreated": 8,
  "notificationsSuppressed": 3
}
```

`notificationsSuppressed` is non-zero on subsequent ticks within the
24h notification window — that's the idempotency unique index doing
its job.

## Local development

```bash
npx wrangler dev --test-scheduled
# then in another shell:
curl -X POST "http://localhost:8787/__scheduled?cron=*+*+*+*+*"
```

Point `ANISYNC_URL` at a tunnel to your local dotnet host (e.g.
`http://host.docker.internal:5000` or a `cloudflared tunnel`), or
just run a local `wrangler dev` against a deployed AniSync.

Tail logs in production with:

```bash
npx wrangler tail
```

## Known limitations

- **Source of truth is AniList only.** The airing schedule comes from
  AniList's `airingSchedules` GraphQL. Shows that exist on MAL/Kitsu
  but not on AniList (rare for currently-airing anime) won't trigger
  notifications. The cross-service id mapping handled by AniSync's
  `AnimeMappingService` covers the MAL/Kitsu → AniList direction.
- **Status filter is `Watching` only.** Planning, Paused, Repeating,
  etc. don't trigger notifications. This is intentional — rewatchers
  don't need a nudge for episodes they've already seen.

## Cost

Cloudflare Workers free tier covers:

- 100,000 requests/day (cron ticks count: 288 ticks/day = negligible)
- 1,000 cron triggers/day (this worker uses 288)
- 10 ms CPU per request (the trigger handler is just a single fetch)

Free tier is overkill here.
