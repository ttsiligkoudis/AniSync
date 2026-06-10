# Working notes

## When something isn't working as expected, search the web first

If a fix doesn't behave as expected — especially platform/framework quirks
(MAUI, Android windowing, native interop, etc.) — do a web search before
guessing again. Others have usually hit the same issue, and the answer
(official docs, GitHub issues, Stack Overflow) is often faster and more
reliable than trial-and-error. Prefer the native/framework-supported API
over manual workarounds once the search surfaces one.

Concrete example from this repo: the player modal not drawing under the
display cutout needed THREE pieces: (1) `LayoutInDisplayCutoutMode.ShortEdges`
(+ `FLAG_LAYOUT_NO_LIMITS`) on the modal's OWN window — since .NET 9 MAUI
hosts modal pages in a DialogFragment whose `Dialog.Window` is separate from
the Activity window, so activity-window flags never reach it; (2) MAUI 10's
`SafeAreaEdges = SafeAreaEdges.None` on the ContentPage; and (3) the same on
the LAYOUTS inside — MAUI 10 applies safe-area insets per-layout, so a root
Grid silently pads its children away from the notch even when the page is
None. Layouts where the inset should pad content but not the background
(scrim bars) should instead KEEP the default and let the inset flow to them.
Manually consuming window insets on the decor broke the layout outright.
On-screen diagnostics (window flags + view positions overlaid on the player)
located piece (3) in one build after several blind attempts.
