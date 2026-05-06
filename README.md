# AniSync

Track your anime progress on Stremio.

AniSync is a [Stremio](https://www.stremio.com/) addon that bridges your anime list provider — [AniList](https://anilist.co/), [Kitsu](https://kitsu.app/), or [MyAnimeList](https://myanimelist.net/) — with Stremio. Browse your lists as Stremio catalogs, edit list entries from inside Stremio, have your watch progress sync back to your provider automatically as you watch, and sync changes across multiple linked providers in one save.

## Features

### Catalogs

Each catalog can be enabled independently and toggled to **Discover Only** to keep it out of the home board while still being available in Discover.

- Currently Watching
- Completed
- Plan to Watch
- On Hold
- Dropped
- Rewatching *(AniList and MyAnimeList — Kitsu has no equivalent)*
- Trending Now
- Seasonal Anime *(this season / next / previous)*
- Airing This Week
- Search

User-list catalogs are always sorted alphabetically by title so the cours of a franchise sit next to each other.

#### Group anime seasons

A configure-page toggle (default ON). When ON, catalog cards use the IMDb / TMDB id from the cross-service mapping and multiple cours of a franchise collapse to one entry — clicking through opens a single per-franchise meta page with every episode. When OFF, catalog cards use the service-native id (`kitsu:N` / `anilist:N` / `mal:N`) so each cour shows as its own card.

### Streams

Optional per-episode add-ons that AniSync attaches to anime in Stremio:

- **Manage Entry** — quick-edit progress, score, notes, rewatch count, start/finish dates without leaving Stremio. For multi-cour franchises the page surfaces a **Season** dropdown so the change targets the right cour, with the cour auto-selected from the URL's season + episode.
- **Auto-track progress** — when you start an episode in Stremio, your list entry on AniList / Kitsu / MyAnimeList is updated automatically (and fanned out to linked secondaries if multi-provider sync is set up).
- **External services** — direct links to Crunchyroll, Netflix, HiDive and other streaming providers. MyAnimeList has no streaming-link field of its own, so AniSync falls back to AniList through the cross-service mapping for MAL users.
- **AniSkip integration** — episode `behaviorHints.skipIntro` / `skipOutro` / `skipRecap` populated from [AniSkip](https://aniskip.com)'s community-curated markers. Compatible Stremio clients (notably Stremio Enhanced) can auto-skip OPs, EDs and recaps.

### Per-episode meta enrichment

Even when you're using ungrouped seasons (`mal:N` / `kitsu:N` / `anilist:N` ids), every episode card carries:

- title, thumbnail, synopsis and air date pulled from Cinemeta
- 🟦 canon / 🟨 filler / 🟧 mixed prefix on the episode title from [AnimeFillerList](https://www.animefillerlist.com)
- `hasScheduledVideos` behavior hint so Stremio renders future-dated episodes with the **Upcoming** badge
- Multi-cour franchises that IMDb flat-numbers (e.g. one IMDb season with 40 episodes covering three split cours) are sliced to the right cour using the franchise's preceding cours' episode counts — opening cour 2 shows that cour's 12 episodes, not all 40.

### Multi-provider sync

Link more than one of your AniList / Kitsu / MyAnimeList accounts and AniSync will keep them in step.

- **Linked accounts** — sign in with additional providers from the configure page; one account is the **primary** (the catalog source) and the rest are kept as secondary writes targets.
- **Make any linked account primary** — click the provider's pill on the configure page. Force-promote handles collisions when the new primary already has different data.
- **Save fan-out** — saving an entry through Manage Entry (or via auto-track) writes to the primary and then concurrently mirrors the change to every linked secondary, with status and score normalised across providers.
- **Sync from primary** — a one-click full-library backfill from primary into every linked secondary, driven from the browser with a progress modal, real cancel (close the modal and the loop stops), and a small client-side worker pool so the run respects each API's rate limit.
- **Lazy re-auth** — refresh tokens are renewed lazily; when a provider's refresh fails, its pill surfaces a **Needs reauth** badge so you can fix it without losing the rest of the configuration.

### Accounts

- Log in with **AniList**, **Kitsu**, or **MyAnimeList** to sync personal lists.
- Or **continue without an account** to use only the public catalogs (Trending, Seasonal, Airing).

Authentication uses each provider's native flow:
- **AniList** — OAuth 2.0 authorization code.
- **Kitsu** — username + password (resource-owner password grant).
- **MyAnimeList** — OAuth 2.0 with PKCE (`code_challenge_method=plain`, the only method MAL accepts).

### Configuration

- **Backups** — export your AniSync configuration to a JSON file or restore from one.
- **Reset / Delete** — wipe toggles back to defaults, or delete the configuration entirely.
- **Disconnect** — clicking the ✗ next to *Connected* signs you out and removes the stored configuration.
- A revision counter is appended to the install URL on every save, so Stremio's manifest cache is invalidated and your changes take effect immediately.

## Installation

Open the AniSync home page in a browser, pick a service (Kitsu / AniList / MyAnimeList), sign in (or continue anonymously), pick the catalogs and streams you want, and click **Install to Stremio** or **Install to Stremio Web**. You can also copy the manifest URL directly.

### MyAnimeList configuration

MAL requires the deployment to register an API client and supply the credentials at runtime. Set the following keys in `appsettings.json`, or as Fly.io secrets using the `Mal__ClientId` / `Mal__ClientSecret` / `Mal__RedirectUri` environment-variable form:

```json
"Mal": {
  "ClientId": "<your MAL client id>",
  "ClientSecret": "<optional — only for confidential MAL apps>",
  "RedirectUri": "https://anisync.fly.dev/Auth/Callback"
}
```

The `RedirectUri` must match the URL registered on the MAL developer dashboard exactly. AniList's credentials are baked into the build for the canonical deployment; Kitsu doesn't need any.

## Tech stack

- ASP.NET Core (.NET 8) MVC web app
- SQLite-backed configuration store
- Anime ID mapping via the community [Fribb anime-lists](https://github.com/Fribb/anime-lists) dataset, enriched at runtime with [manami-project anime-offline-database](https://github.com/manami-project/anime-offline-database) for broader Kitsu / AniList / MAL coverage
- Episode metadata from Stremio's [Cinemeta](https://github.com/Stremio/stremio-addon-cinemeta) addon (sourced from TMDb)
- Filler/canon episode tags scraped from [AnimeFillerList](https://www.animefillerlist.com) (negative-cached so unknown shows don't pound their server)
- Intro/outro skip markers from [AniSkip](https://aniskip.com)
- Deployed on [Fly.io](https://fly.io/)
