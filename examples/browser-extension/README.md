# AniSync Auto-Tracker (browser extension reference)

Reference Chrome / Edge extension that watches anime episodes on **any**
streaming site and auto-updates progress on your linked AniList / Kitsu /
MyAnimeList accounts via the AniSync public API.

This is **not** a published extension — it's a minimal demonstration of how
the API surface composes. Around 300 lines total, no build step.

## How it works

1. The content script runs on every page and watches for a `<video>`
   element. Pages without one are silent no-ops.
2. When a video is found, a chain of heuristics tries to extract
   `{ title, episode }` from the page:
   - **JSON-LD** (`<script type="application/ld+json">` with `@type: TVEpisode`
     — Schema.org, lots of streaming sites publish this for SEO).
   - **OpenGraph meta** (`og:video:series` + `og:video:episode`).
   - **`document.title`** parsed against common shapes
     (`Show — Episode 5`, `Show S1E5`, `Show | Episode 5`).
   - **DOM h1 + page title** as a last resort.
3. A small floating **Track** button appears bottom-right. Click it to see
   what was detected and confirm or correct the title + episode before saving.
4. If the heuristic detection succeeded, the save also fires automatically
   once playback crosses 80%.
5. The background worker hits
   [`GET /api/v1/match`](https://anisync.fly.dev/api/docs) to resolve the
   title into a concrete anime id, then writes progress via
   [`POST /api/v1/me/entries/{id}`](https://anisync.fly.dev/api/docs) with
   the Config UID in the `X-AniSync-Config` header — never in the URL,
   so it can't leak through reverse-proxy / CDN access logs.

AniSync's existing fan-out then mirrors that write to every linked secondary —
no extension-side multi-provider logic needed.

## Files

| File | What it does |
| --- | --- |
| `manifest.json` | MV3 manifest. Declares `<all_urls>` host + content-script match. |
| `content.js` | Heuristic extractors, floating confirm UI, video watcher. |
| `background.js` | Receives messages, calls `/match` and `/entries/{id}`. |
| `options.html` / `options.js` | API base URL + config UID setup page. |

## Install (unpacked)

1. Drop a 128×128 transparent PNG named `icon.png` into this folder (or remove
   the `icons` block from `manifest.json` to skip).
2. Chrome / Edge → `chrome://extensions` → enable **Developer mode** → click
   **Load unpacked** → select this folder.
3. Open the extension's **Options** page, paste your AniSync **Config UID**
   (the long base64url segment in your install URL between the slashes).

## Privacy notes

- The Config UID never leaves your browser except to your AniSync deployment
  via the Options-page URL you set.
- The background worker is the only context that knows your UID — content
  scripts forward `{ title, episode }` only.
- AniSync doesn't store any per-watch telemetry — `/match` is stateless and
  `/entries/{id}` writes go straight to your tracker accounts.

## The `<all_urls>` permission

Chrome shows a stark "this extension can read all your data on every site"
warning at install time. The reason: the universal heuristics need to inspect
the page's DOM / meta / JSON-LD, which means injecting a content script
broadly. The script is a no-op on pages without a `<video>`, but Chrome can't
verify that statically.

If that's too broad for your taste, switch the manifest to
`optional_host_permissions` and prompt-on-demand the first time the user hits
a new domain. More code, friendlier permission story — left as an exercise.

## Detection coverage

The heuristic chain works out-of-the-box on most sites that publish proper
metadata for SEO or social previews. Concretely:

- **Crunchyroll** — JSON-LD `TVEpisode` with `partOfSeries` and
  `episodeNumber` populated; auto-fires reliably.
- **HiDive** — OG meta and document title; usually auto-fires.
- **Netflix** — no clean show + episode markers in the DOM. Heuristics
  return nothing; use the floating Track button to fill in manually.
- **Random fansub site** — varies wildly. The floating Track button is
  always there as a fallback even when nothing's detected.

When the heuristics miss, click the floating button → fix the inputs →
**Save**. The button stays usable on every page that has a `<video>`
element, regardless of whether auto-detect found anything.

## Known limitations

- **Title-to-id matching is fuzzy.** Auto-saves require a `/match` score ≥ 0.5
  to avoid false positives ("Demon Slayer" → "Demon Slayer: Mugen Train").
  Manual saves through the floating button bypass that threshold — when you
  hand-type the title, the extension trusts your judgement.
- **Multi-cour franchises** map a single Crunchyroll season to multiple ids
  on the trackers. The extension picks the highest-scoring match. If you
  watch a sequel that the scraper labels as "Season 2" of an existing show,
  verify the resolved id once via the Track button before relying on it.
- **iframed players** aren't traversed (the manifest sets
  `all_frames: false`). If your favourite site embeds the player in a
  cross-origin iframe, the script can't see it.
