# AniSync — MAUI Blazor Hybrid + Web client

An **exact-look replica** of the AniSync web app, built as a **.NET 9 "MAUI
Blazor Hybrid **and** Web App"** solution: one shared Razor Class Library of
components runs on **Android, Windows, iOS, macOS *and* the web**.

Decisions locked for this build:

| Concern | Choice |
|---|---|
| Data | **Thin client** — talks to the existing ASP.NET app's `/api/v1` JSON API |
| Platforms | Android, Windows, iOS, **+ Web** (shared-RCL template) |
| Player | **LibVLCSharp** on MAUI (software-decodes HEVC/AC3/EAC3/DTS/TrueHD → fixes the no-sound problem); HTML5/ArtPlayer fallback on Web |

---

## ⚠️ Build note (read first)

This repo's cloud container has **no .NET SDK / MAUI workload**, so the platform
heads (which need `dotnet workload install maui` and per-OS toolchains) are **not
generated here** — hand-writing ~50 platform files we can't compile would be
unreliable. What *is* committed is the part that actually constitutes the replica
and is host-agnostic: the **shared `AniSync.Client` Razor Class Library**
(components, design system, API client, player/env seams).

To get a buildable solution locally, generate the two heads from the official
template and reference this library (steps below). The template guarantees
correct, buildable platform plumbing.

---

## Solution layout (target)

```
maui/
  AniSync.MauiBlazor.sln
  src/
    AniSync.Client/      ← shared RCL  (COMMITTED — the app: components, CSS, services)
    AniSync.Maui/        ← MAUI head   (generate from template; references AniSync.Client)
    AniSync.Web/         ← Blazor Web head (generate from template; references AniSync.Client)
```

### What's committed in `AniSync.Client`

```
AniSync.Client.csproj          Razor Class Library (net9.0)
_Imports.razor  Routes.razor
Layout/   MainLayout · AppHeader · BottomNav · SearchBox   (ported from _Layout.cshtml)
Pages/    Home (dashboard) + Library/Discover/Calendar/Settings/Detail stubs
Services/ IAppEnvironment · IMediaPlayer · IAniSyncApi · AniSyncApi (typed HttpClient → /api/v1)
Models/   ApiModels (Suggest / MetaCard DTOs)
wwwroot/  css/site.css (copied verbatim) · img/logo.png · favicon.ico
```

The global **SearchBox** is a complete Blazor port of `site-search.js` (debounced
typeahead → `/api/v1/suggest`) and is the working end-to-end proof of the thin
client. Home wires the **Trending · Anime** shelf to `/api/v1/discover/trending`.

---

## Generating the heads (local, one time)

```bash
# .NET 9 SDK + MAUI workload required
dotnet workload install maui

cd maui
# The single template that emits BOTH a MAUI head and a Blazor Web head sharing one RCL:
dotnet new maui-blazor-web -n AniSync -o .            # VS: "MAUI Blazor Hybrid and Web App"
```

Then **delete the template's generated shared RCL** and point both heads at the
committed `src/AniSync.Client` instead:

```bash
dotnet sln add src/AniSync.Client/AniSync.Client.csproj
dotnet add src/AniSync.Maui  reference src/AniSync.Client/AniSync.Client.csproj
dotnet add src/AniSync.Web   reference src/AniSync.Client/AniSync.Client.csproj
```

In each head's `App.razor`/`index.html`, render the shared design system and use
this library's router:

```html
<!-- <head> -->
<link rel="stylesheet" href="_content/AniSync.Client/css/site.css" />
<meta name="theme-color" content="#0b0d12" />
<!-- before </body> (after the framework script) -->
<script src="_content/AniSync.Client/js/chrome.js"></script>
```
```razor
@* App root *@
<Routes />   @* from AniSync.Client *@
```

`chrome.js` drives the shared shell behaviour (theme toggle, back button, the
mobile "More" sheet, Escape/outside-click closes) via document-level delegation,
so it works no matter when Blazor mounts the layout. The drawer open/close uses
literal `onclick="document.body.classList…"` passthrough, exactly like the web.

---

## DI wiring (per head)

Both heads register the same services; only `IAppEnvironment` and `IMediaPlayer`
differ.

The typed `AniSyncApi` attaches the user's config string as the `X-AniSync-Config`
header (the credential for `/api/v1/me/*`) on every request, reading it from
`AppState.StreamConfig`. That value is persisted per-host through `ISecureStore`
(MAUI `MauiSecureStore` / Web `WebSecureStore`) and hydrated at startup.

```csharp
// shared registrations (call from MauiProgram.cs and Web Program.cs)
builder.Services.AddScoped<AppState>();          // session/nav/media-type/config state
builder.Services.AddScoped<IAniSyncApi, AniSyncApi>();
builder.Services.AddHttpClient<IAniSyncApi, AniSyncApi>((sp, http) =>
{
    var env = sp.GetRequiredService<IAppEnvironment>();
    http.BaseAddress = new Uri(env.ApiBaseUrl);
});
```

### MAUI head (`MauiProgram.cs`)

```csharp
builder.Services.AddMauiBlazorWebView();

builder.Services.AddSingleton<IAppEnvironment>(new MauiAppEnvironment(
    apiBaseUrl: "https://anisync.fly.dev/", isNative: true, supportsNativePlayback: true));

// LibVLCSharp — software decoders for HEVC/AC3/EAC3/DTS/TrueHD.
//   dotnet add AniSync.Maui package LibVLCSharp
//   dotnet add AniSync.Maui package VideoLAN.LibVLC.Android   (Android)
//   dotnet add AniSync.Maui package VideoLAN.LibVLC.Windows   (Windows)
//   (iOS/macOS ship libVLC via the LibVLCSharp build)
LibVLCSharp.Shared.Core.Initialize();
builder.Services.AddSingleton<IMediaPlayer, VlcMediaPlayer>();   // implement against LibVLCSharp.MediaPlayer
```

The native playback page hosts a `LibVLCSharp.MAUI`/`MediaElement` view and is
pushed from `Detail`/`Watch` rather than rendered inside the WebView (a native
surface can't live in the BlazorWebView DOM). `VlcMediaPlayer.PlayAsync` opens
the resolved debrid URL, attaches subtitle tracks, seeks to resume, and raises
position events the watch page uses for scrobble/auto-track.

### Web head (`Program.cs`)

```csharp
builder.Services.AddSingleton<IAppEnvironment>(new WebAppEnvironment(
    apiBaseUrl: "/", isNative: false, supportsNativePlayback: false));
builder.Services.AddScoped<IMediaPlayer, Html5MediaPlayer>();   // JS interop → existing ArtPlayer path
```

---

## API surface to add on the existing server

`/api/v1` already covers the anime side (suggest, search, discover, anime detail,
streams, episodes, subtitles, skip/filler, tags/studios/staff). To finish the
replica, expose the **video/Trakt + dashboard** data as JSON (today they're MVC
partials/JSON on `HomeController`/`LibraryController`):

- `GET /api/v1/stats/anilist`, `GET /api/v1/stats/trakt`  ← the "Your stats" strip
- `GET /api/v1/library?list=&type=`                       ← Library tabs
- `GET /api/v1/shelf?type=&mode=` (trending/popular/anticipated) ← dashboard shelves
- `GET /api/v1/continue-watching?type=`                   ← Continue Watching
- `GET /api/v1/video/{id}` + `GET /api/v1/video/{id}/streams` ← video detail + sources
- Auth: token endpoints + a bearer/cookie scheme the MAUI head stores in secure storage

These return the same DTO shapes already used by the client models.

---

## Roadmap

- [x] **M1 — Foundation**: shared RCL, design system (verbatim `site.css`), service seams (`IAppEnvironment`/`IMediaPlayer`/`IAniSyncApi`), working search typeahead, Home trending slice.
- [x] **M1.5 — Full chrome parity**: exact-DOM port of `_Layout.cshtml` — slide-in drawer + backdrop, full site header (back/logo/nav/search/notif bell/media-type + theme buttons/auth CTA/hamburger), fixed media-type switch, mobile bottom-nav + floating "More" sheet — with real SVG icons, C#-driven active-nav + media-type state (`AppState`), and `chrome.js` for theme/back/more-sheet behaviour.
- [ ] **M2 — Heads + run**: generate MAUI + Web heads from template, wire DI (snippets above + `maui/heads/`), confirm the shell renders identically on Web + Android/Windows.
- [x] **M3 — Stats + shelves**: full Home dashboard — `StatsStrip` (AniList + Trakt rows, real values), `DashboardShelf` (async load, skeletons, hide-when-empty, media-type filter), shelves for Continue watching / New Episodes Today / Trending / Most Popular. All over existing JSON endpoints (`/api/v1/me/stats`, `/api/v1/me/continue-watching`, `/api/v1/airing/today`, `/api/v1/discover`, `/Home/TraktStatsData`); added `popular` to `/api/v1/discover`.
- [x] **M4 — Library + Discover** (Calendar + dedicated search-results still to come): Discover with catalog tabs (Trending/Popular/Seasonal/Airing), genre filter and skip-offset "Load more"; Library with status tabs (Watching/Completed/Planning/Paused/Dropped/Rewatching) + search, over a new `/api/v1/me/list?status=` endpoint (Meta shape, mirrors `continue-watching`).
- [x] **M5 — Detail page** (`/meta/{id}`): hero (backdrop/poster/score/title/info/status/genres), collapsible synopsis, episodes list (→ /watch), streaming-service links, and the **manage-entry modal** (`ManageEntryModal`) wired to the hero tracking pill over `/api/v1/me/entries/{id}` (GET + POST with canonical `ListStatus`).
- [x] **M6 — Watch + LibVLCSharp player** (`/meta/{id}/watch/{ep}`): player surface + prev/next + source picker over the Stremio addon config. Native head plays through LibVLCSharp (`maui/heads/AniSync.Maui/VlcMediaPlayer.cs` + `VlcPlayerPage.cs`) — the HEVC/AC3/EAC3/DTS/TrueHD audio-codec fix; Web head falls back to HTML5 `<video>`. **Resume** persists per-episode via `ISecureStore` (the host player reports progress through `PlaybackRequest.OnProgress/OnEnded`); **auto-play-next** + **auto-track** (toggleable, persisted). Auto-track is the first-party scrobble: at ~90%/episode-end it bumps the tracker entry's progress via `POST /api/v1/me/entries/{id}` (no media-server webhook / scrobble token needed).
- [x] **M7 — Auth + Settings (thin-client model)**: the config string is the credential (`X-AniSync-Config`) AND the Stremio addon config. Settings saves it; persisted via `ISecureStore`, hydrated at startup, so dashboard / Library / Calendar / Watch all authenticate and stream. Full OAuth account management links out to the web account page.
- [x] **Calendar** (`/calendar`): upcoming episodes for the user's list, grouped by day, over `/api/v1/me/upcoming` (projected onto `PosterGrid`).
- [x] **Notifications** (`/notifications` + header bell): list + unread badge + mark-all-read over `/api/v1/notifications`. `NotificationsController.ResolveCurrentAsync` now also accepts the `X-AniSync-Config` header (resolving uid via `Utils.ResolveConfigAsync`), so it works on both the Web head (session) and the MAUI head (config credential). Remaining: PWA/offline parity on the Web head, subtitle UI, push.

### Playback wiring (per head)

The shared **Watch** page calls `IMediaPlayer` only when `IAppEnvironment.SupportsNativePlayback` is true; otherwise it renders an HTML5 `<video>` itself. Drop-in head implementations live in `maui/heads/`:

- **MAUI**: `MauiAppEnvironment`, `VlcMediaPlayer` (+ `VlcPlayerPage`). Register after `LibVLCSharp.Shared.Core.Initialize()`:
  ```csharp
  builder.Services.AddSingleton(_ => new LibVLC());
  builder.Services.AddSingleton<IMediaPlayer, VlcMediaPlayer>();
  builder.Services.AddSingleton<ISecureStore, MauiSecureStore>();
  ```
- **Web**: `WebAppEnvironment`, `Html5MediaPlayer` (no-op — the page renders `<video>`), and `builder.Services.AddScoped<ISecureStore, WebSecureStore>();`.

`AppState.StreamConfig` (the user's Stremio addon config string) gates source resolution; until M7 wires sign-in it stays null and the Watch page shows the "set up streaming" prompt, exactly like the web app.
