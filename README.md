<div align="center">

# AniSync

### Your anime list, synced everywhere — **AniList**, **Kitsu** & **MyAnimeList**, in lockstep.

A self-hostable **web app**, a **Stremio addon**, and a **browser extension** — all talking to one tracker stack so wherever you watch, your list stays right.

[![Install on Stremio](https://img.shields.io/badge/Install-Stremio-7B5BF5?style=for-the-badge&logo=stremio&logoColor=white)](https://anisync.fly.dev)
[![Open the web app](https://img.shields.io/badge/Open-Web%20App-3B82F6?style=for-the-badge)](https://anisync.fly.dev)
[![Browser extension](https://img.shields.io/badge/Get-Browser%20Extension-FF7139?style=for-the-badge&logo=firefox&logoColor=white)](examples/browser-extension)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=.net&logoColor=white)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/github/license/ttsiligkoudis/AniSync?style=for-the-badge)](LICENSE)

[**Three surfaces**](#three-surfaces-one-source-of-truth) · [**Why AniSync**](#why-anisync) · [**vs. other addons**](#how-anisync-compares) · [**Public API**](#public-api) · [**Self-host**](#self-hosting) · [**FAQ**](#faq)

</div>

---

## Why AniSync

If you watch anime online, you've lived this:

> Episode ends. Open a browser tab. Find AniList. Update progress. Realise you also wanted it on MAL because your friends are there. Open another tab. Re-do it. Three episodes later you give up and your list rots for six months.

**AniSync fixes that — for all three trackers at the same time, on every surface you watch from.**

It's the only stack — out of every anime tracker we could find — that:

- Syncs **AniList, Kitsu *and* MyAnimeList** in one install (others pick one).
- **Fans out writes to every linked account** so your lists never drift.
- Lives **in Stremio** *and* **on the web** *and* **in your browser** — same tracker state, three ways to interact with it.
- Ships **AniSkip auto-skip**, **canon/filler tags**, **per-user episode notifications** and **correct cour slicing** out of the box.

---

## Three surfaces, one source of truth

```
┌────────────────────────────────────────────────────────────────────┐
│  🌐 Web App         🎬 Stremio Addon       🧩 Browser Extension     │
│  full client        catalogs + streams    auto-track on any site   │
└──────────────────────────────┬─────────────────────────────────────┘
                               ▼
                  ┌──────────────────────────┐
                  │   AniSync core service   │
                  │  • Multi-provider sync   │
                  │  • Public REST API       │
                  │  • Episode notifications │
                  │  • Plex/Jellyfin webhook │
                  └──────────────────────────┘
                               │
                               ▼
              AniList   •   Kitsu   •   MyAnimeList
```

Sign in once on [anisync.fly.dev](https://anisync.fly.dev). The web app, the Stremio addon and the browser extension all read and write through the same account — your progress on Crunchyroll, on a Stremio stream, on the AniSync watch page, *anywhere* — lands on every tracker you've linked.

---

## 🌐 The Web App

A complete client at [**anisync.fly.dev**](https://anisync.fly.dev) — browse, watch, track, all without leaving the tab. Installable as a PWA on mobile.

### Dashboard
- **Continue watching** shelf pulled live from your primary tracker.
- **Your stats** — counts, mean score, total hours watched, from AniList's `User.statistics`.
- **This Season** — currently airing / new / total counts at a glance.
- **Popular this season / Most anticipated next season / New episodes today** carousels.
- Connected-services pill on every page header — see at a glance which trackers your saves fan out to.

### Library
- Filter your list by status, search by title, narrow by genre.
- Infinite-scroll grid with Continue Watching / Completed / Planning / Paused / Dropped / Rewatching tabs.
- Per-card **Manage Entry** quick-edit modal — same as the Stremio integration, on the web.

### Discover
- Trending Now, Seasonal, Currently Airing.
- Browse by **tag**, **studio**, **staff** — every catalog AniList exposes, in one UI.
- Infinite-scroll pagination throughout.

### Anime detail page (`/anime/{id}`)
- **Hero**: poster, year, score, status badge, your-progress chip, **Continue · Ep N** primary CTA.
- **Episodes** list with thumbnails, air dates, canon/filler tags, future-episode badges.
- **Live filter** — type a number or title fragment, list narrows in place. No network round-trip.
- **Watch movie / Watch first episode** affordances for entries that haven't been started.
- **Recommendations**, **Related (prequels/sequels)**, **Supplementary chips** (staff, studios, tags).
- **Source links** to the same anime on AniList / Kitsu / MAL / IMDb / TMDb / TVDB.
- **Cour-aware episode numbering** — flat-IMDb franchises (Naruto-style) and split-cour franchises both render with sane 1..N within-cour numbering, with the underlying IMDb coordinates preserved for stream lookups.

### Watch page (`/anime/{id}/watch/{ep}`)
- Embedded player with prev/next episode navigation.
- **Stream picker** — debrid sources from your configured addons (Torrentio / MediaFusion / Comet / AIOStreams) + External services (Crunchyroll, Netflix, HiDive).
- **Subtitles** from OpenSubtitles + Wyzie, merged + dedup'd.
- **AniSkip markers** for OP / ED / recap auto-skip.
- **Auto-track** writes progress at 70%, fanned out to every linked provider.

### 🔔 Episode-release notifications
- Bell in the site header with an unread counter.
- **Scheduled** server-side — when an episode you're Watching airs, a notification is created at the airing moment. The bell badge refreshes itself at the right time (no constant polling).
- Click a notification → straight to the watch page for that episode.
- **Dedicated `/notifications` page** with infinite scroll, **multi-select**, **bulk mark-read / delete**, per-row trash.
- Driven by an in-process `BackgroundService` that pulls the AniList airing schedule daily and arms one `Task.Delay` per episode — fires to the second, no external cron.

### Sticky chrome
- Full-bleed sticky header — bell, search and nav stay reachable while you scroll.
- Bottom-nav on mobile (Home / Library / Discover / Settings).

---

## 🎬 The Stremio Addon

Same install URL as the web app — [anisync.fly.dev](https://anisync.fly.dev) — toggle catalogs, click **Install to Stremio**, done. A revision counter on the install URL invalidates Stremio's manifest cache on every save, so no reinstall dance when you change settings.

### 📚 Catalogs

Every catalog can be toggled independently and set to **Discover Only** to keep it out of your home board while staying available in Discover. User-list catalogs are sorted alphabetically so the cours of a franchise sit together.

| Personal lists | Public catalogs |
| --- | --- |
| Currently Watching | Trending Now |
| Completed | Seasonal Anime *(this / next / previous season)* |
| Plan to Watch | Airing This Week |
| On Hold | Search |
| Dropped | |
| Rewatching *(AniList & MAL)* | |

**Group anime seasons** *(toggle, default ON)* — collapses multiple cours of a franchise into a single entry using IMDb/TMDb mapping, so *Attack on Titan* shows up once with every episode. Turn it off to see each cour as its own card.

### 🎬 Streams

Per-episode streams that AniSync attaches to anime in Stremio:

- **Manage Entry** — quick-edit progress, score, notes, rewatch count, start/finish dates without leaving Stremio. Multi-cour franchises get a **Season** dropdown so the change targets the right cour.
- **Auto-track progress** — start an episode and your list updates automatically, fanned out to every linked provider.
- **External services** — direct links to Crunchyroll, Netflix, HiDive and others. MAL doesn't expose these, so AniSync transparently falls back to AniList through the cross-service mapping for MAL users.
- **AniSkip integration** — `skipIntro` / `skipOutro` / `skipRecap` hints from [AniSkip's](https://aniskip.com) community markers. Compatible clients (notably **Stremio Enhanced**) auto-skip OPs, EDs and recaps.

### 🧬 Per-episode metadata enrichment

Even with ungrouped seasons, every episode card carries:

- 🟦 **canon** / 🟨 **filler** / 🟧 **mixed** prefix from [AnimeFillerList](https://www.animefillerlist.com).
- Title, thumbnail, synopsis and air date from Stremio's Cinemeta (sourced from TMDb).
- `hasScheduledVideos` hint so future episodes get the **Upcoming** badge.
- **Correct cour slicing.** IMDb often flat-numbers a multi-cour franchise as one season — AniSync slices it back into the right cour using preceding cours' episode counts. Opening cour 2 shows that cour's 12 episodes, not all 40.

---

## 🧩 The Browser Extension

A cross-browser MV3 extension ([Chrome / Edge / Firefox / Brave](examples/browser-extension)) that watches anime episodes on **any** streaming site and auto-updates your AniSync list as you watch. Works as a bridge from sites Stremio doesn't cover (Crunchyroll, HiAnime, Aniwave, HiDive, …) to your tracker accounts.

### Built-in adapters

| Site | Behavior |
| --- | --- |
| **Crunchyroll** | Auto-tracks across SPA episode navigation, JSON-LD + URL fallback. |
| **HiAnime / Zoro / aniwatch** *(8 hostnames)* | Sidebar-DOM extraction (URL has no episode number). |
| **Aniwave** *(5 hostnames)* | Same shape — sidebar `.active` marker. |
| **HiDive** | Universal heuristic chain (OG meta + title). |
| **Other sites** | Floating **Track** button always available — confirm + save. |

### How it works

1. Content script detects the show + episode via per-site adapter or universal heuristic chain (JSON-LD, URL slug, OG meta, page title, DOM h1).
2. A small **Track** button appears bottom-right whenever the page looks like an episode page.
3. Save auto-fires once playback crosses **80%** (matches Anilist's typical "watched" threshold).
4. Hits AniSync's `GET /api/v1/match` to resolve the title, then `POST /api/v1/me/entries/{id}` to write progress.
5. AniSync's existing fan-out mirrors that write to every linked secondary tracker.

### Permission model

- Curated install-time host allowlist (Crunchyroll, HiAnime, Aniwave, AniSync API).
- **Opt-in flow** for new sites: click the toolbar icon → **Always allow on this site** → browser's native permission dialog → persistent dynamic content-script registration.
- Designed to clear both Chrome Web Store and Mozilla Add-ons review without broad permissions by default.

See [`examples/browser-extension/README.md`](examples/browser-extension/README.md) for the full design, build steps, and how to add a site adapter (~20 lines).

---

## How AniSync compares

The Stremio anime-tracker space is small. Here's how AniSync stacks up against every other tracker addon we could find:

| | **AniSync** | Anime&nbsp;Kitsu | animeo | mal-stremio-addon | AnilistStream |
|---|:---:|:---:|:---:|:---:|:---:|
| **AniList sync** | ✅ | — | ✅ | — | ✅ |
| **Kitsu sync** | ✅ | catalog only | — | — | — |
| **MAL sync** | ✅ | — | — | ✅ | — |
| **Multi-provider sync** | ✅ | — | — | — | — |
| **Edit entries inside Stremio** | ✅ | — | — | — | — |
| **AniSkip auto-skip** | ✅ | — | — | — | — |
| **Canon/filler tags** | ✅ | — | — | — | — |
| **Cour slicing for split seasons** | ✅ | — | — | — | — |
| **Standalone web app** | ✅ | — | — | — | — |
| **Browser extension** | ✅ | — | — | — | — |
| **Episode-release notifications** | ✅ | — | — | — | — |
| **Plex / Jellyfin / Emby webhook** | ✅ | — | — | — | — |
| **Public REST API** | ✅ | — | — | — | — |

> AniSync is **the tracker layer**, not a streaming source. Most users pair it with a scraper addon like Torrentio or Comet for actual playback — see [the recommended stack](#the-anisync-stack) below.

---

## What ties them together

### 🔁 Multi-provider sync

The headline feature. Link more than one tracker and AniSync keeps them aligned.

- **Linked accounts.** Sign in with extra providers from the configure page. One is the **primary** (the catalog source), the rest are secondary write targets.
- **Promote any linked account to primary** with one click. Force-promote handles collisions when the new primary already has different data.
- **Save fan-out.** Manage Entry edits, auto-track writes, browser-extension saves, Plex webhooks — all hit the primary first, then concurrently mirror to every linked secondary with status and score normalised across providers.
- **One-click full sync.** Backfill your entire library from primary into every linked secondary, with a progress modal, real cancel, and a worker pool that respects each provider's rate limit.
- **Lazy re-auth.** When a refresh token fails, that provider's pill shows a **Needs reauth** badge so you can fix it without losing the rest of your config.

### 🏠 Home server sync (Plex / Jellyfin / Emby)

Multi-provider fan-out, but for files you watch outside Stremio and the browser. Paste one webhook URL into your media server and every finished episode scrobbles onto AniList, Kitsu, *and* MyAnimeList simultaneously.

- **One URL, all three servers.** Content-type-sniffs the payload — Plex sends multipart, Jellyfin and Emby send JSON. Same URL works for whichever you run.
- **External-id resolution.** Plex's default agent emits TVDB IDs, HAMA emits AniDB, Jellyfin's Skyhook plugin emits AniDB, IMDB and TMDB on top — AniSync resolves any of them to your tracker primary via the bundled cross-mapping data.
- **Plex Home filtering.** Optional username field — events from other Plex Home users are dropped.
- **Idempotent.** A 60-second dedup window per (anime, season, episode) shrugs off retries and resumed-session re-deliveries.
- **Token-revocable.** A "Rotate token" button on the configure page invalidates the old URL instantly without touching your tracker auth.

### 🔐 Accounts

Each provider uses its own native auth flow — no AniSync intermediary, no password storage we don't need:

| Provider | Flow |
| --- | --- |
| **AniList** | OAuth 2.0 authorization code |
| **Kitsu** | Username + password (resource-owner password grant) |
| **MyAnimeList** | OAuth 2.0 with PKCE *(`code_challenge_method=plain` — the only method MAL accepts)* |

Or skip auth entirely and use public catalogs only.

### ⚙️ Configuration

- **Backups** — export the full configuration to JSON or restore from one.
- **Reset / Delete** — wipe toggles back to defaults, or remove the configuration entirely.
- **Disconnect** — click the ✗ next to *Connected* to sign out.

---

## Public API

Every feature the site uses ships behind a Swagger-documented JSON API at `/api/v1/*`. The browser extension is built entirely on it — your custom dashboard or scrobble script can be too.

**Explore the interactive docs at [`/api/docs`](https://anisync.fly.dev/api/docs).**

| Surface | Endpoints |
| --- | --- |
| **Anonymous read** | `/anime/{id}`, `/anime/{id}/episodes`, `/anime/{id}/streams`, `/anime/{id}/trailer`, `/anime/{id}/related`, `/anime/{id}/recommendations`, `/anime/{id}/supplementary`, `/anime/{id}/links`, `/anime/{id}/episodes/{ep}/subtitles`, `/search`, `/match`, `/discover/{kind}`, `/discover/by-tag/{tag}`, `/tags`, `/studios`, `/studios/{id}/anime`, `/staff/{id}/anime`, `/airing/today`, `/airing/upcoming`, `/stats/season`, `/skip/{id}/{ep}`, `/filler/{id-or-title}`, `/mappings/{id}` |
| **User-scoped** *(via `X-AniSync-Config` header)* | `/me/library`, `/me/entries/{id}`, `/me/entries` *(bulk)*, `/me/sync/diff`, `/me/sync`, `/me/primary/{service}`, `/me/linked`, `/me/stats`, `/me/continue-watching`, `/me/upcoming` |
| **Notifications** *(session-based)* | `/notifications`, `/notifications/count`, `/notifications/{id}/read`, `/notifications/read-all`, `/notifications/bulk-read`, `/notifications/bulk-delete`, `/notifications/{id}` *(DELETE)* |

UID is passed via the `X-AniSync-Config` header — never in the URL — so it can't leak through Referer, reverse-proxy logs, browser history, or shared screenshots.

---

## Install in 30 seconds

### As a Stremio addon
1. Open **[anisync.fly.dev](https://anisync.fly.dev)**.
2. Pick **AniList**, **Kitsu** or **MyAnimeList** and sign in. *(Or skip auth and use the public catalogs.)*
3. Toggle the catalogs and streams you want.
4. Click **Install to Stremio** — done.

### As a web app
Open **[anisync.fly.dev](https://anisync.fly.dev)** in your browser. Sign in. That's it. On mobile, tap the install icon in the address bar to add it as a PWA.

### As a browser extension
```bash
# Chrome / Edge / Brave
chrome://extensions → enable Developer mode → Load unpacked → select examples/browser-extension/

# Firefox
about:debugging#/runtime/this-firefox → Load Temporary Add-on → pick manifest.json
```

Then open the extension Options page and paste your Config UID (the long segment in your install URL between the slashes). Full setup in [the extension README](examples/browser-extension/README.md).

---

## The AniSync stack

AniSync is one piece of an anime-on-Stremio setup. Here's the stack we recommend:

```
┌─────────────────────────────────────────────────────┐
│  Stremio Enhanced  ←  player with AniSkip support   │
├─────────────────────────────────────────────────────┤
│  AniSync           ←  tracker + list management     │  ← you are here
├─────────────────────────────────────────────────────┤
│  Torrentio / Comet ←  streams (with Real-Debrid)    │
└─────────────────────────────────────────────────────┘
```

- **Stremio Enhanced** (or any AniSkip-aware client) — honours the skip-intro/outro hints AniSync embeds.
- **AniSync** — your lists, your progress, your tracker writes.
- **Torrentio / Comet** (with a debrid service) — actual playback sources.

You can also keep **Anime Kitsu** installed for its public Kitsu catalogs alongside AniSync — the two don't conflict.

---

## FAQ

<details>
<summary><strong>Does AniSync stream anime?</strong></summary>

No, and that's deliberate. AniSync is the tracker layer — it manages your AniList/Kitsu/MAL lists and enriches episode metadata. Pair it with a streaming addon like Torrentio or Comet for playback. Scraper addons get DMCA'd; trackers don't.
</details>

<details>
<summary><strong>I changed something on the configure page but Stremio still shows the old catalogs.</strong></summary>

AniSync appends a revision counter to the manifest URL on every save, so this should self-resolve. If it doesn't, uninstall the addon in Stremio and reinstall from the configure page — Stremio aggressively caches manifests and a manual reinstall always wins.
</details>

<details>
<summary><strong>Why does my MyAnimeList list show Crunchyroll links?</strong></summary>

MAL doesn't expose streaming-provider links on its API. To avoid leaving MAL users with no external links, AniSync looks up the same anime on AniList through the cross-service ID mapping and surfaces those. The titles are the same anime — just different metadata sources.
</details>

<details>
<summary><strong>AniSkip auto-skip isn't working.</strong></summary>

Stock Stremio doesn't honour `skipIntro` / `skipOutro` / `skipRecap` hints — you need a client that does. **Stremio Enhanced** is the easiest option. The hints are still embedded for any other client that adds support later. The AniSync web-app watch page honours them natively.
</details>

<details>
<summary><strong>Will AniSync write to my list when I scrub forward, or only when I genuinely watch?</strong></summary>

Auto-track fires on Stremio's play event for a new episode (or, in the browser extension, at 80% playback) — scrubbing within an episode doesn't trigger writes. If you want fully manual control, disable Auto-track and use Manage Entry instead.
</details>

<details>
<summary><strong>Is multi-provider sync bidirectional?</strong></summary>

No. There's a single **primary** provider that is the source of truth; secondaries receive writes but their changes don't flow back. Promote a different account to primary if you want to flip the direction. The full-sync button does a one-shot backfill from primary to all secondaries.
</details>

<details>
<summary><strong>How do episode-release notifications work?</strong></summary>

The web app pulls the AniList airing schedule daily and arms one in-process `Task.Delay` per future episode. When the timer fires, the dispatcher walks your Watching list and inserts a notification row if the episode matches anything you're tracking. The bell badge refreshes itself precisely at the next known airing time — no constant polling. New notifications are visible the moment you next interact with the page.
</details>

<details>
<summary><strong>Where is my data stored?</strong></summary>

In a SQLite database on the AniSync server, keyed by your install ID. Provider tokens are stored to keep your sync working; nothing is shared with third parties. Want full control? [Self-host](#self-hosting).
</details>

<details>
<summary><strong>Rewatching catalog is missing on Kitsu.</strong></summary>

Kitsu's data model has no rewatch state, so the catalog is hidden for Kitsu accounts. AniList and MAL both support it.
</details>

<details>
<summary><strong>I see "Needs reauth" on a provider.</strong></summary>

Your refresh token expired or was revoked. Click the provider's pill on the configure page and re-authenticate — the rest of your configuration is preserved.
</details>

<details>
<summary><strong>I have other anime addons installed and Stremio is showing their (sparser) meta instead of AniSync's.</strong></summary>

Stremio queries every installed addon that declares the same id prefix and picks based on **addon order in your install list** — there's no manifest field that lets an addon claim "I'm the canonical source for these ids". For best results, place AniSync **at the top of your addon order**, or at minimum directly below Cinemeta.

The official Stremio UI doesn't expose drag-to-reorder for installed addons. The community-built [**Stremio Addon Manager**](https://stremio-addon-manager.vercel.app/) does — log in with your Stremio account, drag AniSync above any other anime-tracker addon, save, and restart Stremio so the manifest cache invalidates.

Symptom this fixes: a meta page that shows 1 episode where AniSync would emit 14, or a poster/synopsis that looks wrong for the cour you opened.
</details>

---

## Self-hosting

AniSync is an ASP.NET Core MVC app and ships with a `Dockerfile` and `fly.toml`. The canonical deployment is on Fly.io, but anything that runs a .NET 8 container will do.

### MyAnimeList configuration

MAL requires the deployment to register an API client. Set these in `appsettings.json`, or as environment variables / Fly secrets using the `Mal__ClientId` form:

```json
"Mal": {
  "ClientId":     "<your MAL client id>",
  "ClientSecret": "<optional — only for confidential MAL apps>",
  "RedirectUri":  "https://your-deployment.example.com/Auth/Callback"
}
```

The `RedirectUri` must match the URL registered on the MAL developer dashboard exactly. AniList credentials are baked into the build for the canonical deployment; Kitsu doesn't need any.

### Storage

A single SQLite file at `$ANISYNC_DATA_DIR/anisync.db` holds every config: token data, linked secondaries, addon URLs, notifications, watching cache. Mount it to a persistent volume on your host. Fly.io's mount in `fly.toml` already does this.

### Optional Cloudflare Workers

Two small Workers ship alongside the app — both optional, both free to run:

- [`cf-cors-proxy/`](cf-cors-proxy) — adds CORS headers to debrid CDN responses so the watch page's matroska-subtitles extractor can stream MKV bytes from the browser.
- [`cf-mkv-extractor/`](cf-mkv-extractor) — fetches MKV files via Range requests, parses the EBML index, returns SSA/ASS/SRT subtitle tracks as JSON (saves ~700 MB vs. full-file proxy).

---

## Tech stack

- **ASP.NET Core (.NET 8)** MVC web app — site, addon, API, and notification scheduler all in one process.
- **SQLite**-backed configuration store + notification queue.
- Anime ID mapping via [Fribb anime-lists](https://github.com/Fribb/anime-lists), enriched at runtime with [manami-project anime-offline-database](https://github.com/manami-project/anime-offline-database).
- Episode metadata from Stremio's [Cinemeta](https://github.com/Stremio/stremio-addon-cinemeta) (TMDb).
- Filler/canon tags scraped from [AnimeFillerList](https://www.animefillerlist.com), negative-cached so unknown shows don't pound their server.
- Intro/outro markers from [AniSkip](https://aniskip.com).
- Subtitles from [OpenSubtitles](https://opensubtitles.com) + [Wyzie](https://sub.wyzie.ru) (Subdl / Addic7ed federated).
- Stream addons via the Stremio addon protocol — Torrentio, MediaFusion, Comet, Jackettio, AIOStreams all work.
- Deployed on [Fly.io](https://fly.io).

## Acknowledgements

AniSync stands on the shoulders of the open anime-data community:

- [Fribb anime-lists](https://github.com/Fribb/anime-lists) and [manami-project anime-offline-database](https://github.com/manami-project/anime-offline-database) for cross-service ID mapping
- [AniSkip](https://aniskip.com) for community-curated intro/outro markers
- [AnimeFillerList](https://www.animefillerlist.com) for canon/filler tagging
- [Stremio](https://www.stremio.com) and the Cinemeta addon for the platform and base metadata

---

<div align="center">

**Found a bug? Want a feature? [Open an issue.](https://github.com/ttsiligkoudis/AniSync/issues)**

<sub>Built by <a href="https://github.com/ttsiligkoudis">@ttsiligkoudis</a> · Not affiliated with AniList, Kitsu, MyAnimeList or Stremio</sub>

</div>
