# AniSync Auto-Tracker (browser extension reference)

Reference cross-browser extension (Chrome / Edge / Firefox / Brave) that
watches anime episodes on **any** streaming site and auto-updates progress
on your linked AniList / Kitsu / MyAnimeList accounts via the AniSync
public API.

This is a **reference implementation** — a clean starting point for a
publishable extension that follows Chrome Web Store + Mozilla Add-ons
review patterns (curated install-time hosts + opt-in via
`permissions.request` from a toolbar popup). Single MV3 manifest, no build
step.

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
| `manifest.json` | Cross-browser MV3 manifest. Curated host allowlist for known adapters + `optional_host_permissions: ["<all_urls>"]` for opt-in extension. Declares both `background.scripts` (Firefox event page) and `background.service_worker` (Chrome) so one file works on both browsers. `browser_specific_settings.gecko` provides the Firefox add-on ID. |
| `content.js` | Site adapters, universal heuristic extractors, floating confirm UI, video watcher. |
| `background.js` | `/match` + `/entries/{id}` calls, dynamic content-script registration on grant, restart-time rehydration of opt-in registry. |
| `popup.html` / `popup.js` | Toolbar popup — the cross-browser permission gateway. Calls `permissions.request` directly from a click handler so it works identically on Chrome and Firefox. |
| `options.html` / `options.js` | API base URL + config UID setup page. |

## Install (unpacked)

1. Drop a 128×128 transparent PNG named `icon.png` into this folder (or remove
   the `icons` block from `manifest.json` to skip).
2. Load the extension:
   - **Chrome / Edge / Brave**: open `chrome://extensions` → enable
     **Developer mode** → click **Load unpacked** → select this folder.
   - **Firefox**: open `about:debugging#/runtime/this-firefox` → click
     **Load Temporary Add-on…** → select `manifest.json`. (Temporary
     installs are wiped at browser restart; for persistence, either
     submit to AMO for signing or run Developer Edition / Nightly with
     `xpinstall.signatures.required = false`.)
3. Open the extension's **Options** page, paste your AniSync **Config UID**
   (the long base64url segment in your install URL between the slashes).
4. Visit Crunchyroll / HiAnime / Aniwave to see auto-tracking work out of
   the box. For other sites, click the AniSync icon in the toolbar — a
   small popup appears with two buttons:
   - **Track this episode** — injects the tracker into the current tab
     for this session only (`activeTab` permission, no host grant).
   - **Always allow on this site** — triggers the browser's native
     permission dialog and, on grant, registers a persistent dynamic
     content script so future visits auto-inject without the popup.

## Privacy notes

- The Config UID never leaves your browser except to your AniSync deployment
  via the Options-page URL you set.
- The background worker is the only context that knows your UID — content
  scripts forward `{ title, episode }` only.
- AniSync doesn't store any per-watch telemetry — `/match` is stateless and
  `/entries/{id}` writes go straight to your tracker accounts.

## Permission model

Designed to clear both Chrome Web Store and Mozilla Add-ons review — no
broad permissions by default, every grant is user-initiated, single code
path on both browsers.

**Default (install-time) permissions.** Three function-only permissions
plus a curated host allowlist:

- `storage` — saves your Config UID and the list of opted-in sites.
- `activeTab` — when the popup is open, lets `chrome.scripting.executeScript`
  inject `content.js` into the active tab one-shot. Standard reviewer-
  friendly pattern.
- `scripting` — needed by `chrome.scripting.executeScript` (one-shot
  injection from the popup) and `chrome.scripting.registerContentScripts`
  (persistent registration after an opt-in grant).
- Host allowlist (the three adapter sites): Crunchyroll, HiAnime/Zoro/
  aniwatch (8 hostnames), Aniwave (5 hostnames), and `anisync.fly.dev` for
  the API. Each is explicitly justified by an in-extension adapter.

**Optional permissions.** `optional_host_permissions: ["<all_urls>"]` is
declared but not granted. It's *available to grant* per-host through
`permissions.request`, but a user installing the extension never sees the
"read all your data" warning at install time — only when they explicitly
opt into a new site, and only via the browser's native permission dialog
(not ours).

**Opt-in flow on a non-pre-baked site:**

1. User visits a streaming site we don't yet have a permission for.
2. Nothing happens automatically — the extension is dormant on that host.
3. User clicks the AniSync toolbar icon → `popup.html` opens.
4. The popup queries `chrome.permissions.contains` for the active tab's
   origin and shows two buttons:
   - **Track this episode** — calls `chrome.scripting.executeScript` to
     inject `content.js` into the active tab using the `activeTab`
     permission the popup click just granted. One-shot, this tab only,
     gone on navigation.
   - **Always allow on this site** — calls `chrome.permissions.request({
     origins: ['*://example.com/*'] })` directly. The browser's native
     permission dialog appears.
5. On grant, the popup messages `background.js` with `anisync:register-site`.
   The background worker calls `chrome.scripting.registerContentScripts` to
   make the injection persistent and records the origin in
   `chrome.storage.local`. The popup then injects `content.js` for the
   current session so the user gets immediate value without a refresh.
6. On extension updates (which wipe the dynamic-script registry) the
   background worker re-registers all opted-in origins on
   `runtime.onInstalled` / `runtime.onStartup`, filtered against
   `chrome.permissions.getAll()` so revoked permissions don't get
   resurrected.

**Why the popup, not an in-page button?** `permissions.request` requires a
live user-input gesture in the same JS frame as the call. Chrome propagates
gestures through `runtime.sendMessage` chains so a content-script click can
reach into the background worker; **Firefox does not**. A popup is the
smallest cross-browser surface where the gesture and the API call live in
the same frame, so a single code path works on both.

**Revoking access.** The browser's extensions page shows every host the
extension has access to under "Site access" — users can flip any
individual site off or remove it entirely. The background worker
reconciles its dynamic-script registry against `chrome.permissions.getAll()`
on startup so revocations take effect on the next browser launch.

## Cross-browser notes

**Manifest portability.** The `background` block declares both
`scripts: ["background.js"]` (Firefox event page) and
`service_worker: "background.js"` (Chrome). Chrome ignores the `scripts`
key in MV3; Firefox ignores `service_worker` on versions that don't
support it. One file, one manifest, two browsers. Targeted at
`strict_min_version: "115.0"` (the first Firefox MV3 release) — the
script intentionally avoids any service-worker-only API.

**API namespace.** Code uses the `chrome.*` namespace throughout. Firefox
exposes both `chrome.*` and `browser.*` to MV3 extensions, with `chrome.*`
matching Chrome's callback signature. No webextension-polyfill is needed
because we don't rely on promise-style returns from listener-shaped APIs.

**`browser_specific_settings.gecko`.** Required by AMO for signing.
`anisync@anisync.dev` is the placeholder add-on ID — change it before
submitting to your own AMO listing.

**`permissions.request` semantics.** Both browsers refuse the call unless
it's inside a user-input handler. Both return a boolean (Chrome via
callback, Firefox via promise — `chrome.*` polyfills the callback shape on
Firefox so the same code works). The popup's click handler is the only
context that satisfies this on both browsers.

**Match-pattern shape.** We use `*://${hostname}/*` so http and https both
work — anime sites mix the two, and registering only one scheme would
silently miss the other. Both browsers accept this pattern in
`permissions.request` and `scripting.registerContentScripts`.

**Privileged URLs.** Both browsers refuse content-script injection on
internal pages: Chrome blocks `chrome://`, `edge://`, the Web Store, and
`view-source:`; Firefox blocks `about:`, `moz-extension://`, and
`addons.mozilla.org`. The popup's `isInjectableUrl` check disables both
buttons with an explanatory message on those pages.

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
