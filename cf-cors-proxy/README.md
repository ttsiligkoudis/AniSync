# AniSync CORS Proxy

Cloudflare Worker that adds `Access-Control-Allow-Origin: *` to responses
from debrid CDNs (Real-Debrid, AllDebrid, Premiumize, etc.) so the AniSync
watch page's matroska-subtitles extractor can stream MKV bytes from the
browser. Without this proxy, those hosts block `fetch()` cross-origin
even though `<video>` can play them, so embedded SSA/ASS extraction is
off on debrid streams by default.

The worker is host-locked to the debrid CDNs AniSync already knows about
(see `ALLOWED_HOST_SUFFIXES` in `src/index.js`) so it can't be used as a
generic open proxy.

## Prerequisites

- A free Cloudflare account
- Node.js 18+ (only on the deploy machine — runtime is Cloudflare's)

## One-time setup

```bash
cd cf-cors-proxy
npm install
npx wrangler login           # opens a browser tab; sign in to Cloudflare
npx wrangler deploy
```

Wrangler prints the deployed URL, e.g.:

```
Deployed anisync-cors-proxy triggers
  https://anisync-cors-proxy.<your-subdomain>.workers.dev
```

Copy that URL.

## Point AniSync at the worker

Set the `CORS_PROXY_URL` environment variable on the AniSync host to the
worker URL above. Method depends on how you run AniSync:

- **Fly.io**:
  ```bash
  fly secrets set CORS_PROXY_URL=https://anisync-cors-proxy.<sub>.workers.dev
  ```
- **Docker / docker-compose**: add to the `environment:` block.
- **systemd unit**: add `Environment=CORS_PROXY_URL=...`.
- **Local dev (`dotnet run`)**: export it in your shell first.

Restart AniSync. The Watch page now uses the proxy for debrid streams,
and the embedded-subtitle status pill (`Looking for embedded subtitles…`)
will appear again on those sources.

## Optional: lock it down with a secret

By default the worker accepts any caller and just relies on the host
allowlist. If you want to be extra cautious (the worker URL is in your
deployed HTML, so anyone who views source can call it), gate it with a
shared secret:

```bash
npx wrangler secret put PROXY_SECRET
# enter a random string when prompted
```

Then on the AniSync host:

```bash
fly secrets set CORS_PROXY_SECRET=<same string>
```

(Or whatever your platform's equivalent is.) AniSync appends
`&secret=<value>` to every proxied URL; the worker rejects mismatches
with `401`.

## Verifying it works

After deploy + AniSync restart:

1. Open a debrid-streamed episode.
2. Pick a source.
3. The embedded-subtitle status pill should appear:
   - `Looking for embedded subtitles…` (loading)
   - Then either a count or `No embedded subtitles in this file`.
4. If the source's MKV has SSA/ASS tracks, they show up in the
   subtitles menu labelled `<lang> SSA` alongside the OpenSubtitles /
   Wyzie entries.

If the pill stays loading forever or you see CORS errors in DevTools:

- Check the worker logs: `npx wrangler tail` while you reload the page.
- Confirm the host hitting the worker matches one of the
  `ALLOWED_HOST_SUFFIXES`. Add new ones to `src/index.js` and redeploy
  if a new debrid provider needs supporting.

## Cost

Cloudflare Workers free tier covers:

- 100,000 requests/day
- Unlimited subrequest bandwidth (within fair use)
- 10 ms CPU per request

The worker just forwards bytes — almost no CPU usage. Personal usage
fits the free tier comfortably.
