<div align="center">

# AniSync

### The only Stremio addon that syncs anime across **AniList**, **Kitsu** & **MyAnimeList** — simultaneously.

Browse your lists as native Stremio catalogs. Edit entries without leaving the app. Watch an episode and your progress writes back to every linked tracker, automatically.

[![Install on Stremio](https://img.shields.io/badge/Install-Stremio-7B5BF5?style=for-the-badge&logo=stremio&logoColor=white)](https://anisync.fly.dev)
[![Install on Stremio Web](https://img.shields.io/badge/Install-Stremio%20Web-5A4FD8?style=for-the-badge)](https://anisync.fly.dev)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=.net&logoColor=white)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/github/license/ttsiligkoudis/AniSync?style=for-the-badge)](LICENSE)

[**Install**](#install-in-30-seconds) · [**Why AniSync**](#why-anisync) · [**vs. other addons**](#how-anisync-compares) · [**Features**](#features) · [**FAQ**](#faq)

</div>

---

## Why AniSync

If you watch anime on Stremio, you've lived this:

> Episode ends. Open a browser tab. Find AniList. Update progress. Realise you also wanted it on MAL because your friends are there. Open another tab. Re-do it. Three episodes later you give up and your list rots for six months.

**AniSync fixes that, for all three trackers at the same time.**

It's the only Stremio addon — out of every anime tracker we could find — that:

- Syncs **AniList, Kitsu *and* MyAnimeList** in one install (others pick one).
- **Fans out writes to every linked account** so your lists never drift.
- Lets you **edit entries inside Stremio** via a per-episode stream (no addon-SDK hacks, no alt-tabbing).
- Ships **AniSkip auto-skip**, **canon/filler tags**, and **correct cour slicing** for split-season franchises out of the box.

It's a tracker layer for your existing Stremio setup — not a scraper, not a streaming source, not a replacement for Torrentio. It just makes sure your anime list reflects what you actually watched.

---

## Install in 30 seconds

1. Open **[anisync.fly.dev](https://anisync.fly.dev)**.
2. Pick **AniList**, **Kitsu** or **MyAnimeList** and sign in. *(Or skip auth and use the public catalogs.)*
3. Toggle the catalogs and streams you want.
4. Click **Install to Stremio** — done.

A revision counter is appended to the install URL on every save, so Stremio's manifest cache invalidates automatically. No reinstall dance when you change settings.

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

> AniSync is **the tracker layer**, not a streaming source. Most users pair it with a scraper addon like Torrentio or Comet for actual playback — see [the recommended stack](#the-anisync-stack) below.

---

## Features

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

- **Manage Entry** — quick-edit progress, score, notes, rewatch count, start/finish dates without leaving Stremio. Multi-cour franchises get a **Season** dropdown so the change targets the right cour, auto-selected from the URL.
- **Auto-track progress** — start an episode and your list updates automatically, fanned out to every linked provider.
- **External services** — direct links to Crunchyroll, Netflix, HiDive and others. MAL doesn't expose these, so AniSync transparently falls back to AniList through the cross-service mapping for MAL users.
- **AniSkip integration** — `skipIntro` / `skipOutro` / `skipRecap` hints from [AniSkip's](https://aniskip.com) community markers. Compatible clients (notably **Stremio Enhanced**) auto-skip OPs, EDs and recaps.

### 🧬 Per-episode metadata enrichment

Even with ungrouped seasons, every episode card carries:

- 🟦 **canon** / 🟨 **filler** / 🟧 **mixed** prefix from [AnimeFillerList](https://www.animefillerlist.com).
- Title, thumbnail, synopsis and air date from Stremio's Cinemeta (sourced from TMDb).
- `hasScheduledVideos` hint so future episodes get the **Upcoming** badge.
- **Correct cour slicing.** IMDb often flat-numbers a multi-cour franchise as one season — AniSync slices it back into the right cour using preceding cours' episode counts. Opening cour 2 shows that cour's 12 episodes, not all 40.

### 🔁 Multi-provider sync

The headline feature. Link more than one tracker and AniSync keeps them aligned.

- **Linked accounts.** Sign in with extra providers from the configure page. One is the **primary** (the catalog source), the rest are secondary write targets.
- **Promote any linked account to primary** with one click. Force-promote handles collisions when the new primary already has different data.
- **Save fan-out.** Manage Entry edits and auto-track writes hit the primary, then concurrently mirror to every linked secondary with status and score normalised across providers.
- **One-click full sync.** Backfill your entire library from primary into every linked secondary, with a progress modal, real cancel, and a worker pool that respects each provider's rate limit.
- **Lazy re-auth.** When a refresh token fails, that provider's pill shows a **Needs reauth** badge so you can fix it without losing the rest of your config.

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

## Screenshots

> _Drop captures into `docs/screenshots/` and uncomment the block below._

<!--
<div align="center">

| Configure page | Manage Entry stream | Catalog in Stremio |
| :---: | :---: | :---: |
| ![Configure](docs/screenshots/configure.png) | ![Manage Entry](docs/screenshots/manage-entry.png) | ![Catalog](docs/screenshots/catalog.png) |

</div>
-->

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

Stock Stremio doesn't honour `skipIntro` / `skipOutro` / `skipRecap` hints — you need a client that does. **Stremio Enhanced** is the easiest option. The hints are still embedded for any other client that adds support later.
</details>

<details>
<summary><strong>Will AniSync write to my list when I scrub forward, or only when I genuinely watch?</strong></summary>

Auto-track fires on Stremio's play event for a new episode — scrubbing within an episode doesn't trigger writes. If you want fully manual control, disable Auto-track and use Manage Entry instead.
</details>

<details>
<summary><strong>Is multi-provider sync bidirectional?</strong></summary>

No. There's a single **primary** provider that is the source of truth; secondaries receive writes but their changes don't flow back. Promote a different account to primary if you want to flip the direction. The full-sync button does a one-shot backfill from primary to all secondaries.
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

---

## Tech stack

- **ASP.NET Core (.NET 8)** MVC web app
- **SQLite**-backed configuration store
- Anime ID mapping via [Fribb anime-lists](https://github.com/Fribb/anime-lists), enriched at runtime with [manami-project anime-offline-database](https://github.com/manami-project/anime-offline-database)
- Episode metadata from Stremio's [Cinemeta](https://github.com/Stremio/stremio-addon-cinemeta) (TMDb)
- Filler/canon tags scraped from [AnimeFillerList](https://www.animefillerlist.com), negative-cached so unknown shows don't pound their server
- Intro/outro markers from [AniSkip](https://aniskip.com)
- Deployed on [Fly.io](https://fly.io)

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
