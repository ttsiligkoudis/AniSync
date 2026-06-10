# Working notes

## When something isn't working as expected, search the web first

If a fix doesn't behave as expected — especially platform/framework quirks
(MAUI, Android windowing, native interop, etc.) — do a web search before
guessing again. Others have usually hit the same issue, and the answer
(official docs, GitHub issues, Stack Overflow) is often faster and more
reliable than trial-and-error. Prefer the native/framework-supported API
over manual workarounds once the search surfaces one.

Concrete example from this repo: the player modal not drawing under the
display cutout needed BOTH MAUI 10's `ContentPage.SafeAreaEdges =
SafeAreaEdges.None` (stops MAUI's own safe-area padding) AND
`LayoutInDisplayCutoutMode.ShortEdges` set on the modal's OWN window —
since .NET 9 MAUI hosts modal pages in a DialogFragment whose
`Dialog.Window` is separate from the Activity window, so activity-window
flags never reach it. Manually consuming window insets on the decor broke
the layout outright. Searches surfaced both halves immediately.
