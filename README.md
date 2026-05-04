# AniSync

Track your anime progress on Stremio.

AniSync is a [Stremio](https://www.stremio.com/) addon that bridges your anime list provider — [AniList](https://anilist.co/), [Kitsu](https://kitsu.app/), or [MyAnimeList](https://myanimelist.net/) — with Stremio. Browse your lists as Stremio catalogs, edit list entries from inside Stremio, and have your watch progress sync back to your provider automatically as you watch.

## Features

### Catalogs

Expose your anime lists as Stremio catalogs. Each catalog can be enabled independently, and toggled to **Discover Only** to keep it out of the home board while still being available in Discover.

- Currently watching
- Completed
- Plan to Watch
- On Hold
- Dropped
- Rewatching *(AniList and MyAnimeList)*
- Trending now
- Seasonal Anime
- Airing This Week

### Streams

Optional per-episode add-ons that AniSync attaches to anime in Stremio:

- **Manage Entry** — quick-edit progress, score and notes for a list entry without leaving Stremio.
- **Auto-track progress** — when you start an episode in Stremio, your list entry on AniList / Kitsu / MyAnimeList is updated automatically.
- **External services** — surface direct links to Crunchyroll, Netflix, HiDive and other streaming providers. MyAnimeList has no streaming-link field of its own, so AniSync transparently falls back to AniList through the cross-service mapping for MAL users.

### Accounts

- Log in with your **AniList**, **Kitsu**, or **MyAnimeList** account to sync personal lists.
- Or **continue without an account** to use only the public catalogs (Trending, Seasonal, Airing).

Authentication uses each provider's native flow:
- **AniList** — OAuth 2.0 authorization code.
- **Kitsu** — username + password (resource owner password grant).
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

- ASP.NET Core (.NET) MVC web app
- SQLite-backed configuration store
- Anime ID mapping via the community [Fribb anime-lists](https://github.com/Fribb/anime-lists) dataset, enriched with [manami-project anime-offline-database](https://github.com/manami-project/anime-offline-database) for broader Kitsu / AniList / MAL coverage
- Metadata enrichment via TMDb
- Deployed on [Fly.io](https://fly.io/)
