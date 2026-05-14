# AniSync MKV Subtitle Extractor

Cloudflare Worker that does an **indexed extraction** of embedded
SSA / ASS / SRT subtitles from a debrid-CDN MKV URL — Range-fetches
~10-30 MB of an MKV instead of the full 700 MB the streaming
`cf-cors-proxy` + `matroska-subtitles` pipeline would pull.

Works by walking the file's own EBML index:

1. Fetch first ~256 KB → parse EBML header + Segment start + SeekHead.
2. Use SeekHead's pointers to range-fetch the Tracks element → find
   subtitle TrackEntries and grab their CodecPrivate (ASS header).
3. Range-fetch the Cues element near EOF → get the cluster byte
   offsets for every subtitle cue.
4. For each subtitle cluster, range-fetch just that cluster (typically
   < 1 MB) and parse its SimpleBlocks / BlockGroups for our tracks.
5. Return a JSON payload with per-track cues:

```json
{
  "tracks": [
    {
      "number": 4,
      "language": "eng",
      "name": "English [SubsPlease]",
      "codecID": "S_TEXT/ASS",
      "header": "[Script Info]\n…",
      "cues": [
        { "time": 1230, "duration": 4000, "text": "0,0,Default,,…" },
        …
      ]
    }
  ],
  "stats": { "fileSize": 753218764, "trackCount": 11 }
}
```

The AniSync watch page calls this Worker first; if it returns 404
(file has no SeekHead / Cues index) the client falls back to the
existing `cf-cors-proxy` + `matroska-subtitles` streaming path.

## Prerequisites

- A free Cloudflare account
- Node.js 18+ on the deploy machine

## Deploy

```bash
cd cf-mkv-extractor
npm install
npx wrangler login            # opens browser, sign in
npx wrangler deploy
```

Wrangler prints the deployed URL, e.g.:

```
Deployed anisync-mkv-extractor triggers
  https://anisync-mkv-extractor.<your-sub>.workers.dev
```

Copy that URL.

## Point AniSync at the worker

Set `MKV_EXTRACTOR_URL` on the AniSync host:

```bash
# Fly.io
fly secrets set MKV_EXTRACTOR_URL=https://anisync-mkv-extractor.<sub>.workers.dev

# Docker / docker-compose — add to environment:
MKV_EXTRACTOR_URL=https://anisync-mkv-extractor.<sub>.workers.dev

# Local dotnet run
export MKV_EXTRACTOR_URL=https://anisync-mkv-extractor.<sub>.workers.dev
```

Restart AniSync. The watch page will route embedded-sub extraction
through this worker first; if the response is 404 / 502 it falls
back to `cf-cors-proxy` (which must still be set up — this Worker
doesn't replace it).

## Optional: lock down with a shared secret

```bash
npx wrangler secret put PROXY_SECRET
# enter a random string
```

Then on AniSync:

```bash
fly secrets set MKV_EXTRACTOR_SECRET=<same string>
```

AniSync appends `&secret=…` to every extractor URL; mismatches
return 401. Same pattern as the cf-cors-proxy `CORS_PROXY_SECRET`.

## Verifying it works

After deploy + AniSync restart:

1. Open a debrid episode → pick a source.
2. The status pill goes `Waiting for stream URL…` → resolve fires.
3. With the extractor available you should see:
   `Looking for embedded subtitles…` → `N embedded tracks ready`
   in **a few seconds** instead of the minute-plus the streaming
   path took. The pill jumps faster because we only pulled ~10 MB.
4. The Subtitles selector shows the embedded tracks with their
   real names ("English [SubsPlease] SSA", etc.).

To watch the Worker live:

```bash
cd cf-mkv-extractor
npx wrangler tail
```

Then reload the watch page. Each extraction logs one request entry
with status 200 (success) or 404 (indexless — fell back) or 502
(parse error).

## When it falls back

| Worker response | Why | Client behaviour |
|---|---|---|
| `200` + JSON | File has SeekHead + Cues, parse succeeded | Use these tracks; cf-cors-proxy not consulted |
| `404` + `indexless: true` | File has no SeekHead or no Cues pointer (rare on modern muxes) | Fall back to streaming via cf-cors-proxy + matroska-subtitles |
| `502` | Parse error (corrupted Cues, unknown codec, etc.) | Fall back to streaming |
| `403` | Target host not in allowlist | No fallback; embedded extraction skipped |

## Cost

Cloudflare Workers Free covers this comfortably:

- 100k requests/day. Each watch-pick = 1 extractor call.
- Unlimited subrequest bandwidth on Workers Free.
- 10 ms CPU per request. The EBML parser uses ~5-30 ms per file —
  well under the cap. (The slow part is Range fetches, which don't
  count toward CPU time.)

For a 700 MB MKV: typical extraction transfers 10-30 MB through CF,
takes 2-5 seconds wall-clock, uses ~20 ms CPU.

## Edge cases & known limits

- **Files with no SeekHead**: rare on modern muxes (mkvmerge always
  emits one) but possible from old encoders or streaming muxers.
  Returns 404 indexless → client streams instead.
- **Encrypted / DRM-wrapped tracks**: not supported. Returns 502.
- **Non-standard subtitle codecs**: PGS, VobSub etc. are returned in
  the tracks list but their `cues[].text` is the raw binary payload
  which the AniSync client can't render. Filtered out client-side.
- **Files > 8 EB**: the EBML var-int parser caps at 8-byte sizes.
  Won't hit this in practice.

## Repo layout

```
cf-mkv-extractor/
  src/
    index.js     — Worker entry: routing, auth, CORS, error mapping
    reader.js    — Range-aware HTTP reader with chunk cache
    ebml.js      — EBML var-int + element header parser
    mkv.js       — Matroska walker: SeekHead → Tracks → Cues → blocks
  wrangler.toml  — Worker config
  package.json   — wrangler dev/deploy scripts
```
