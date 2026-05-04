# AniSync

Track your anime progress on Stremio.

AniSync is a [Stremio](https://www.stremio.com/) addon that bridges your anime list provider — [AniList](https://anilist.co/) or [Kitsu](https://kitsu.app/) — with Stremio. Browse your lists as Stremio catalogs, edit list entries from inside Stremio, and have your watch progress sync back to your provider automatically as you watch.

## Features

### Catalogs

Expose your anime lists as Stremio catalogs. Each catalog can be enabled independently, and toggled to **Discover Only** to keep it out of the home board while still being available in Discover.

- Currently watching
- Completed
- Plan to Watch
- On Hold
- Dropped
- Rewatching *(AniList only)*
- Trending now
- Seasonal Anime
- Airing This Week

### Streams

Optional per-episode add-ons that AniSync attaches to anime in Stremio:

- **Manage Entry** — quick-edit progress, score and notes for a list entry without leaving Stremio.
- **Auto-track progress** — when you start an episode in Stremio, your list entry on AniList/Kitsu is updated automatically.
- **External services** — surface direct links to Crunchyroll, Netflix, HiDive and other streaming providers.

### Accounts

- Log in with your **AniList** or **Kitsu** account to sync personal lists.
- Or **continue without an account** to use only the public catalogs (Trending, Seasonal, Airing).

### Configuration

- **Backups** — export your AniSync configuration to a JSON file or restore from one.
- **Reset / Delete** — wipe toggles back to defaults, or delete the configuration entirely.
- A revision counter is appended to the install URL on every save, so Stremio's manifest cache is invalidated and your changes take effect immediately.

## Installation

Open the AniSync home page in a browser, sign in (or continue anonymously), pick the catalogs and streams you want, and click **Install to Stremio** or **Install to Stremio Web**. You can also copy the manifest URL directly.

## Tech stack

- ASP.NET Core (.NET) MVC web app
- SQLite-backed configuration store
- Anime ID mapping via the community [anime-lists](https://github.com/Fribb/anime-lists) dataset
- Metadata enrichment via TMDb
- Deployed on [Fly.io](https://fly.io/)
