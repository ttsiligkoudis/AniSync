# AniSync on Android TV (and Android phones)

AniSync is already an installable **PWA** (`/manifest.webmanifest` + a service
worker). This folder packages that same PWA into an Android **APK** you can
sideload onto an **Android TV / Google TV / Fire TV** box — giving you a real
app icon in the launcher that opens AniSync full-screen.

The web UI ships with **D-pad / remote navigation** (`wwwroot/js/tv-navigation.js`):
on a TV it auto-detects the platform, draws a large focus ring, and walks the
poster grid with the arrow keys + OK. No extra setup needed for that part.

> **Playback note.** A TV WebView plays **MP4 / H.264** well but **MKV inline
> is unreliable** — exactly like the iOS case. Lean on the built-in
> **"Open externally"** action, which on Android TV fires the intent chooser
> into **VLC / Kodi / Infuse**; those play the debrid MKV stream natively.

---

## Two ways to build the APK

You only need **one** of these.

### Option A — PWABuilder (easiest, no local toolchain)

1. Deploy AniSync to its public HTTPS domain.
2. Go to <https://www.pwabuilder.com>, enter the URL, let it score the manifest.
3. Click **Package for stores → Android**, download the `.apk`/`.aab` + the
   generated signing key (keep `signing.keystore` + the password safe — you
   need the *same* key for every future update).
4. Jump to **[Sideloading onto the TV](#sideloading-onto-the-tv)**.

### Option B — Bubblewrap CLI (uses the config in this folder)

Prereqs: **Node 18+**, a **JDK 17**, and the **Android SDK** (Bubblewrap can
fetch the JDK/SDK for you on first run).

```bash
npm install -g @bubblewrap/cli

# 1. Edit twa/twa-manifest.json — replace every YOUR-DOMAIN.example with your
#    real host. (host = bare domain; the *Url fields = full https URLs.)

# 2. Scaffold the Android project from the config:
bubblewrap init --manifest ./twa/twa-manifest.json
#    Accept the prompts; when asked, let it generate a signing key at
#    ./android.keystore with alias "android" (matches the manifest's signingKey).

# 3. Build:
bubblewrap build
#    → produces app-release-signed.apk (sideload this) and an .aab (for Play).

# 4. Grab the signing fingerprint for assetlinks.json (see below):
bubblewrap fingerprint
```

---

## Make it a first-class TV app (Leanback)

A vanilla TWA/Bubblewrap build targets phones. Sideloaded apps **still appear**
on Google TV (under *Apps → see all*) and run fine, so this section is optional
polish — but it puts the app on the **TV home rows** with a proper banner.

Edit the generated `app/src/main/AndroidManifest.xml`:

```xml
<!-- TVs have no touchscreen; declaring it not-required keeps the app eligible -->
<uses-feature android:name="android.hardware.touchscreen" android:required="false" />
<uses-feature android:name="android.software.leanback" android:required="false" />

<application
    android:banner="@drawable/banner"   <!-- add a 320×180 xhdpi banner PNG -->
    ... >
    <activity ... >
        <intent-filter>
            <action android:name="android.intent.action.MAIN" />
            <!-- existing phone launcher entry -->
            <category android:name="android.intent.category.LAUNCHER" />
            <!-- add the TV launcher entry -->
            <category android:name="android.intent.category.LEANBACK_LAUNCHER" />
        </intent-filter>
    </activity>
</application>
```

Then rebuild (`bubblewrap build`, or `./gradlew assembleRelease`).

---

## Sideloading onto the TV

1. On the TV: **Settings → System → About →** click *Android version* /
   *Build* a few times to unlock **Developer options**, then enable
   **Developer options → USB / Network debugging**.
2. Note the TV's IP (Settings → Network).
3. From your computer (with `adb` from the Android SDK platform-tools):

```bash
adb connect <TV-IP>:5555
adb install -r app-release-signed.apk
```

The icon now shows in the launcher. (Alternatively: copy the APK to a USB
stick / cloud drive and install it via a file-manager app like *Downloader* /
*Send files to TV* — handy when `adb` isn't convenient.)

---

## Removing the address bar (Digital Asset Links)

Without this, the app still works but briefly shows the page URL (Custom Tab
fallback). To run as a true full-screen TWA:

1. Get your signing key's SHA-256 (`bubblewrap fingerprint`, or
   `keytool -list -v -keystore ./android.keystore -alias android`).
2. Copy `twa/assetlinks.template.json` → fill in the fingerprint (and confirm
   `package_name` matches `packageId` = `com.anisync.twa`).
3. Serve the result at `https://YOUR-DOMAIN.example/.well-known/assetlinks.json`.

### Serving assetlinks.json from ASP.NET Core

⚠️ **Gotcha:** `UseStaticFiles` does **not** serve dot-prefixed directories
(`.well-known`) by default, so dropping the file in `wwwroot/.well-known/` is
**not** enough. Add an explicit static-file provider for it in `Program.cs`,
*before* the existing `app.UseStaticFiles(...)` call:

```csharp
using Microsoft.Extensions.FileProviders;

// Serve /.well-known/* (Digital Asset Links for the Android TWA, ACME, etc.).
// The default static-files provider skips dot-directories, so map it explicitly.
var wellKnown = Path.Combine(app.Environment.WebRootPath, ".well-known");
if (Directory.Exists(wellKnown))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(wellKnown),
        RequestPath = "/.well-known",
        ServeUnknownFileTypes = true   // assetlinks has no extension-based type on some setups
    });
}
```

Then put your filled-in file at `wwwroot/.well-known/assetlinks.json` and
verify it returns `200 application/json` at the public URL. Google's
[Statement List Tester](https://developers.google.com/digital-asset-links/tools/generator)
confirms the association.

---

## Testing the D-pad UI without a TV

Append `?tv=1` to any AniSync URL on desktop (e.g.
`https://YOUR-DOMAIN.example/discover?tv=1`) — that forces TV mode (stored in
`localStorage`), so you can drive the whole app with just the arrow keys +
Enter and see the focus ring. Use `?tv=0` to turn it back off.

## Files in this folder

| File | Purpose |
| --- | --- |
| `twa-manifest.json` | Bubblewrap build config (edit the host, then `bubblewrap init`). |
| `assetlinks.template.json` | Digital Asset Links template — fill in the fingerprint, deploy under `.well-known`. |
| `README.md` | This guide. |
