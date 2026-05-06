<div align="center">

# AniSync

**Your anime list, native in Stremio.**

Browse your AniList, Kitsu or MyAnimeList catalogs inside Stremio, edit list entries without leaving the app, and have your watch progress sync back automatically — across all three providers at once if you want.

[![Install on Stremio](https://img.shields.io/badge/Install-Stremio-7B5BF5?style=for-the-badge&logo=stremio)](https://anisync.fly.dev)
[![Install on Stremio Web](https://img.shields.io/badge/Install-Stremio%20Web-5A4FD8?style=for-the-badge)](https://anisync.fly.dev)
[![License](https://img.shields.io/github/license/ttsiligkoudis/AniSync?style=for-the-badge)](LICENSE)

[Install](#install) · [Features](#features) · [Screenshots](#screenshots) · [FAQ](#faq) · [Self-hosting](#self-hosting)

</div>

---

## Why AniSync

Most anime addons either show you a public catalog *or* sync to one provider. AniSync does both, and it does it for all three of the major list services in one install:

- **One addon, three providers.** AniList, Kitsu, MyAnimeList — sign in with whichever you use, link as many as you like, and AniSync keeps them in step.
- **List management without leaving Stremio.** Update progress, score, notes, rewatch count and dates from a stream attached to every episode.
- **Smart episode metadata.** Filler/canon tags, intro/outro skip markers, upcoming-episode badges, correct cour slicing for split-season franchises.
- **Public catalogs too.** Trending, Seasonal and Airing This Week work without an account.

## Install

1. Open **[anisync.fly.dev](https://anisync.fly.dev)** in your browser.
2. Pick a provider — **AniList**, **Kitsu** or **MyAnimeList** — and sign in. (Or skip sign-in and use the public catalogs only.)
3. Toggle the catalogs and streams you want.
4. Click **Install to Stremio** or **Install to Stremio Web**, or copy the manifest URL.

A revision counter is appended to the install URL on every save, so Stremio's manifest cache is invalidated and your changes take effect immediately — no reinstall dance.

## Features

### Catalogs

Each catalog can be enabled independently and toggled to **Discover Only** to keep it out of the home board while still being available in Discover. User-list catalogs are sorted alphabetically so the cours of a franchise sit next to each other.

| Personal lists | Public catalogs |
| --- | --- |
| Currently Watching | Trending Now |
| Completed | Seasonal Anime *(this / next / previous season)* |
| Plan to Watch | Airing This Week |
| On Hold | Search |
| Dropped | |
| Rewatching *(AniList & MAL only)* | |

**Group anime seasons** *(toggle, default ON)* — uses IMDb/TMDb ids from the cross-service mapping so multiple cours of a franchise collapse to a single entry with every episode under one meta page. Turn it off to see each cour as its own card with native `kitsu:`/`anilist:`/`mal:` ids.

### Streams

Optional per-episode streams that AniSync attaches to anime in Stremio:

- **Manage Entry** — quick-edit progress, score, notes, rewatch count and start/finish dates without leaving Stremio. Multi-cour franchises get a **Season** dropdown so the change targets the right cour, auto-selected from the URL.
- **Auto-track progress** — start an episode in Stremio and your list entry updates automatically, fanned out to every linked provider.
- **External services** — direct links to Crunchyroll, Netflix, HiDive and others. MAL has no streaming-link field of its own, so AniSync transparently falls back to AniList through the cross-service mapping for MAL users.
- **AniSkip integration** — `skipIntro` / `skipOutro` / `skipRecap` behaviour hints populated from [AniSkip](https://aniskip.com)'s community markers. Compatible clients (notably **Stremio Enhanced**) auto-skip OPs, EDs and recaps.

### Per-episode metadata enrichment

Even with ungrouped seasons, every episode card carries:

- Title, thumbnail, synopsis and air date from Stremio's Cinemeta (sourced from TMDb).
- 🟦 canon / 🟨 filler / 🟧 mixed prefix from [AnimeFillerList](https://www.animefillerlist.com).
- `hasScheduledVideos` hint so future episodes get the **Upcoming** badge.
- Correct cour slicing — IMDb often flat-numbers a multi-cour franchise as one season; AniSync slices it back into the right cour using preceding cours' episode counts. Opening cour 2 shows that cour's 12 episodes, not all 40.

### Multi-provider sync

Link multiple AniList / Kitsu / MAL accounts and AniSync keeps them aligned.

- **Linked accounts.** Sign in with extra providers from the configure page. One is the **primary** (the catalog source), the rest are secondary write targets.
- **Promote any linked account to primary** with one click. Force-promote handles collisions when the new primary already has different data.
- **Save fan-out.** Manage Entry edits and auto-track writes hit the primary, then concurrently mirror to every linked secondary with status and score normalised across providers.
- **One-click full sync.** Backfill your entire library from primary into every linked secondary, with a browser-driven progress modal, real cancel (close the modal, the loop stops), and a small worker pool that respects each provider's rate limit.
- **Lazy re-auth.** Refresh tokens renew on demand; when one fails, that provider's pill shows a **Needs reauth** badge so you can fix it without losing the rest of the configuration.

### Accounts

Each provider uses its own native auth flow:

| Provider | Flow |
| --- | --- |
| **AniList** | OAuth 2.0 authorization code |
| **Kitsu** | Username + password (resource-owner password grant) |
| **MyAnimeList** | OAuth 2.0 with PKCE (`code_challenge_method=plain` — the only method MAL accepts) |

Or skip auth entirely and use the public catalogs (Trending, Seasonal, Airing).

### Configuration

- **Backups** — export your full configuration to JSON or restore from one.
- **Reset / Delete** — wipe toggles back to defaults, or remove the configuration entirely.
- **Disconnect** — click the ✗ next to *Connected* to sign out and remove stored configuration.

## Screenshots

> _Screenshots coming soon — drop your captures into a `docs/screenshots/` folder and reference them here._

<!--
| Configure page | Manage Entry stream | Catalog in Stremio |
| :---: | :---: | :---: |
| ![Configure](docs/screenshots/configure.png) | ![Manage Entry](docs/screenshots/manage-entry.png) | ![Catalog](docs/screenshots/catalog.png) |
-->

## FAQ

<details>
<summary><strong>I changed something on the configure page but Stremio still shows the old catalogs.</strong></summary>

AniSync appends a revision counter to the manifest URL on every save, so this should self-resolve. If it doesn't, uninstall the addon in Stremio and reinstall from the configure page — Stremio aggressively caches manifests and a manual reinstall always wins.
</details>

<details>
<summary><strong>Why is my MyAnimeList list showing Crunchyroll links it shouldn't have?</strong></summary>

MAL doesn't expose streaming-provider links on its API. To avoid leaving MAL users with no external links, AniSync looks up the same anime on AniList through the cross-service ID mapping and surfaces those. The titles are the same anime — just different metadata sources.
</details>

<details>
<summary><strong>AniSkip auto-skip isn't working.</strong></summary>

Stock Stremio doesn't honour `skipIntro` / `skipOutro` / `skipRecap` behaviour hints — you need a client that does. **Stremio Enhanced** is the easiest option. The hints are still embedded for any other client that adds support later.
</details>

<details>
<summary><strong>Will AniSync write to my list when I scrub forward, or only when I genuinely watch?</strong></summary>

Auto-track fires when Stremio reports a play event for a new episode — scrubbing within an episode doesn't trigger writes. If you want fully manual control, disable Auto-track and use the Manage Entry stream instead.
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
<summary><strong>I see "Needs reauth" on one of my providers.</strong></summary>

Your refresh token expired or was revoked. Click the provider's pill on the configure page and re-authenticate — the rest of your configuration is preserved.
</details>

<details>
<summary><strong>Rewatching catalog is missing on Kitsu.</strong></summary>

Kitsu's data model has no rewatch state, so the catalog is hidden for Kitsu accounts. AniList and MAL both support it.
</details>

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

## Tech stack

- **ASP.NET Core (.NET 8)** MVC web app
- **SQLite**-backed configuration store
- Anime ID mapping via the [Fribb anime-lists](https://github.com/Fribb/anime-lists) dataset, enriched at runtime with [manami-project anime-offline-database](https://github.com/manami-project/anime-offline-database) for broader Kitsu / AniList / MAL coverage
- Episode metadata from Stremio's [Cinemeta](https://github.com/Stremio/stremio-addon-cinemeta) (TMDb)
- Filler / canon tags scraped from [AnimeFillerList](https://www.animefillerlist.com), negative-cached so unknown shows don't pound their server
- Intro / outro skip markers from [AniSkip](https://aniskip.com)
- Deployed on [Fly.io](https://fly.io)

## Acknowledgements

AniSync stands on the shoulders of the open anime-data community:

- [Fribb anime-lists](https://github.com/Fribb/anime-lists) and [manami-project anime-offline-database](https://github.com/manami-project/anime-offline-database) for cross-service ID mapping
- [AniSkip](https://aniskip.com) for community-curated intro/outro markers
- [AnimeFillerList](https://www.animefillerlist.com) for canon/filler tagging
- [Stremio](https://www.stremio.com) and the Cinemeta addon for the platform and base metadata

---

<div align="center">
<sub>Built by <a href="https://github.com/ttsiligkoudis">@ttsiligkoudis</a> · <a href="https://github.com/ttsiligkoudis/AniSync/issues">Report a bug</a> · <a href="https://github.com/ttsiligkoudis/AniSync/issues">Request a feature</a></sub>
</div>
