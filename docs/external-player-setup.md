# Direct VLC launch from "Open with…" (one-time setup)

When you click **Open with…** on a debrid source, AniSync first
tries to launch VLC directly via the `vlc://` URL handler. If
that handler isn't registered on your system the browser silently
ignores the call and AniSync falls back to a `.m3u` download
(which still opens VLC, just with an extra click).

To get the direct one-click launch on **Windows**, register the
handler once:

## Windows

Save the snippet below as `register-vlc-url-handler.reg` and
double-click it. Confirm the UAC prompt. Done.

```reg
Windows Registry Editor Version 5.00

[HKEY_CLASSES_ROOT\vlc]
@="URL:VLC Protocol"
"URL Protocol"=""

[HKEY_CLASSES_ROOT\vlc\shell]
@=""

[HKEY_CLASSES_ROOT\vlc\shell\open]
@=""

[HKEY_CLASSES_ROOT\vlc\shell\open\command]
@="\"C:\\Program Files\\VideoLAN\\VLC\\vlc.exe\" \"%1\""
```

Adjust the path if you installed VLC somewhere other than
`C:\Program Files\VideoLAN\VLC`. To undo, delete the
`HKEY_CLASSES_ROOT\vlc` key.

## macOS

VLC.app's `Info.plist` already declares `vlc` as a URL scheme on
recent installs — no extra setup needed. If it doesn't work,
re-install VLC from videolan.org and let Launch Services rebuild
the handler database (`/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister -kill -r -domain local -domain system -domain user`).

## Linux

Most distros auto-register VLC's `vlc://` handler via the
`vlc.desktop` file shipped in `/usr/share/applications/`. If the
direct launch doesn't fire, check that VLC's desktop entry
includes `x-scheme-handler/vlc=vlc.desktop` in
`~/.config/mimeapps.list` (or run `xdg-mime default vlc.desktop x-scheme-handler/vlc`).

## What if I don't want to set this up?

You don't have to. The `.m3u` download fallback already opens
VLC (or whatever player owns the `.m3u` association) without any
configuration — it's just an extra click on the downloaded file.
