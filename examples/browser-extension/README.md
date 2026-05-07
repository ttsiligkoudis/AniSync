# AniSync Auto-Tracker (browser extension reference)

Reference Chrome / Edge extension that watches anime episodes on streaming
sites and auto-updates progress on your linked AniList / Kitsu / MyAnimeList
accounts via the AniSync public API.

This is **not** a published extension — it's a minimal, ~100-line
demonstration of how the API surface composes:

1. Content script extracts `{ title, episode }` from the page's DOM.
2. Background worker resolves the title to an anime id via
   [`GET /api/v1/match`](https://anisync.fly.dev/api/docs).
3. Once the video crosses 80% playback, it writes progress via
   [`POST /api/v1/users/{config}/entries/{id}`](https://anisync.fly.dev/api/docs).

AniSync's existing fan-out then mirrors that write to every linked secondary —
no extension-side multi-provider logic needed.

## Files

| File | What it does |
| --- | --- |
| `manifest.json` | MV3 manifest; declares the content scripts and host permissions. |
| `content.js` | Per-site title + episode extractors and the `<video>` watcher. |
| `background.js` | Receives messages, calls `/match` and `/entries/{id}`. |
| `options.html` / `options.js` | API base URL + config UID setup page. |

## Install (unpacked)

1. Drop a 128×128 transparent PNG named `icon.png` into this folder (or remove
   the `icons` block from `manifest.json` to skip).
2. Chrome / Edge → `chrome://extensions` → enable **Developer mode** → click
   **Load unpacked** → select this folder.
3. Open the extension's **Options** page, paste your AniSync **Config UID**
   (the long base64url segment in your install URL between the slashes).

## Adding a new site

`content.js` has a `SITES` map keyed on hostname suffix. Each entry returns
either `{ title, episode }` or `null` when the page doesn't have a clean
extraction path. To support a new site, add a new entry:

```js
'examplestream.tv': () => {
  const title = document.querySelector('.show-title')?.textContent?.trim();
  const ep = Number(document.title.match(/Episode (\d+)/i)?.[1]);
  return title && ep ? { title, episode: ep } : null;
}
```

…and add the matching pattern to `manifest.json`'s `content_scripts.matches`
and `host_permissions`.

## Privacy notes

- The Config UID never leaves your browser except to your AniSync deployment
  via the Options-page URL you set.
- The content script runs only on the streaming sites in `manifest.json`. The
  background worker is the only context that knows your UID.
- AniSync doesn't store any per-watch telemetry — the `/match` endpoint is
  stateless and `/entries/{id}` writes go straight to your tracker accounts.

## Known limitations

- **Title-to-id matching is fuzzy.** A score below 0.5 is rejected to avoid
  false positives. Stream sites with very short / generic titles ("Naruto",
  "Bleach") may need the threshold tightened or a custom site extractor that
  pulls the franchise's full romanised title.
- **Netflix isn't fully supported** — its DOM doesn't expose a clean
  show + episode pair. The site is listed in the manifest as a placeholder
  for users who want to add their own extractor.
- **Multi-cour franchises** can map a single Crunchyroll season to multiple
  anime ids on the trackers. The extension picks the highest-scoring match;
  if you watch a sequel that the scraper labels as "Season 2", verify the
  resolved id once via the Options-page **Test** button (TODO) before relying
  on it.
