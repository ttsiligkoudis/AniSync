# Working notes

## When something isn't working as expected, search the web first

If a fix doesn't behave as expected — especially platform/framework quirks
(MAUI, Android windowing, native interop, etc.) — do a web search before
guessing again. Others have usually hit the same issue, and the answer
(official docs, GitHub issues, Stack Overflow) is often faster and more
reliable than trial-and-error. Prefer the native/framework-supported API
over manual workarounds once the search surfaces one.

Concrete example from this repo: the player modal not drawing under the
display cutout was solved by MAUI 10's `ContentPage.SafeAreaEdges =
SafeAreaRegions.None`, not by manually consuming Android window insets
(which broke the layout). A quick search would have found this immediately.
