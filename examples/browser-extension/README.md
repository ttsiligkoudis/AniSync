# AniSync Auto-Tracker (browser extension reference)

Reference Chrome / Edge extension that watches anime episodes on **any**
streaming site and auto-updates progress on your linked AniList / Kitsu /
MyAnimeList accounts via the AniSync public API.

This is **not** a published extension — it's a minimal demonstration of how
the API surface composes. Around 500 lines total, no build step.

## How it works

1. The content script runs on every page. The detection pipeline is:
   - **Per-site adapter** (first match wins). Adapters live in
     `content.js` under `ADAPTERS = [...]`; each declares the hostnames
     it owns and an `extract()` that returns `{ title, episode }` or null.
     Adapters return null on non-watch pages so navigation around a
     site's home/settings doesn't fire spurious detections.
   - **Universal heuristic chain** (fallback). Runs whenever no adapter
     matches the host or the matched adapter returned null:
     - **JSON-LD** (`<script type="application/ld+json">` with
       `@type: TVEpisode` — Schema.org, lots of streaming sites publish
       this for SEO).
     - **URL slug** (`/show-name-episode-7/`, `/watch/slug/7`, …).
     - **OpenGraph meta** (`og:video:series` + `og:video:episode`).
     - **`document.title`** parsed against common shapes
       (`Show — Episode 5`, `Show S1E5`, `Show | Episode 5`).
     - **DOM h1 + page title** as a last resort.
2. A small floating **Track** button appears bottom-right whenever the
   page looks like an episode page. Click it to see what was detected
   and confirm or correct the title + episode before saving.
3. If detection succeeded, the save also fires automatically once
   playback crosses 80%.
4. SPA route changes (Crunchyroll, HiAnime, Aniwave, …) re-run detection
   without a full page reload — the script patches `history.pushState` /
   `replaceState` and listens to `popstate`, then resets the auto-fire
   flag if the new (title, episode) differs.
5. A `MutationObserver` watches for `<video>` elements being added or
   for `loadedmetadata` events on a single element whose `src` swaps
   between episodes (common SPA player pattern).
6. The background worker hits
   [`GET /api/v1/match`](https://anisync.fly.dev/api/docs) to resolve the
   title into a concrete anime id, then writes progress via
   [`POST /api/v1/me/entries/{id}`](https://anisync.fly.dev/api/docs) with
   the Config UID in the `X-AniSync-Config` header — never in the URL,
   so it can't leak through reverse-proxy / CDN access logs.

AniSync's existing fan-out then mirrors that write to every linked secondary —
no extension-side multi-provider logic needed.

## Built-in site adapters

| Adapter | Hostnames | What the adapter does that the universal chain doesn't |
| --- | --- | --- |
| **Crunchyroll** | `*.crunchyroll.com` | Locale-prefixed watch URL gating; document.title fallback when JSON-LD is delayed-loaded by the SPA. |
| **HiAnime / Zoro / aniwatch** | `hianime.{to,tv,nz,bz}`, `aniwatch{,tv}.to`, `zoro.{to,in}` | Episode number lives only in the sidebar (URL has opaque `?ep=12345`). Adapter pulls it from the active item's `.ssli-order` or `data-number`. |
| **Aniwave** (formerly 9anime) | `aniwave.{to,li,bz,cx,se}` | Same shape as HiAnime — sidebar `ul.episodes` with `.active` marker. |

Adding a new adapter is ~20 lines: append an entry to the `ADAPTERS`
array in `content.js` with the host(s) it matches and an `extract()` that
returns `{ title, episode, source }`. Look at the HiAnime adapter as a
template for sidebar-DOM patterns.

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

| Site | Path | Notes |
| --- | --- | --- |
| **Crunchyroll** | dedicated adapter + JSON-LD | SPA navigation handled — auto-fires reliably across consecutive episodes without a reload. |
| **HiAnime / Zoro / aniwatch** | dedicated adapter | Sidebar-DOM extraction. URL has no episode number, so the universal chain alone wouldn't work. |
| **Aniwave** | dedicated adapter | Similar to HiAnime; verify selectors after major site updates. |
| **HiDive** | universal (OG meta + title) | Usually auto-fires. |
| **Netflix** | universal misses | DOM has no clean show + episode markers; use the floating Track button. |
| **Random fansub site** | universal (URL slug) | Varies wildly. Floating Track button always available as fallback. |

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
  cross-origin iframe, the script can't see it. Manual save via the
  floating button still works because it triggers off the URL / DOM,
  not the `<video>` element.
- **Adapter selectors will rot.** Streaming sites occasionally rename
  classes or restructure their sidebar DOM. When a site adapter stops
  finding the active episode, open DevTools on the watch page, locate
  the active sidebar item, and update the relevant adapter in
  `content.js`. The universal chain should keep auto-fire working in
  the meantime via the URL slug or document.title fallbacks.
