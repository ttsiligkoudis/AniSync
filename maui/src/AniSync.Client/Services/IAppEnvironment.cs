namespace AniSync.Client.Services;

/// <summary>
/// Per-host environment seam. Each head (MAUI / Web) supplies its own
/// implementation via DI so the shared UI doesn't have to know whether it's
/// running inside a native WebView or a browser.
/// </summary>
public interface IAppEnvironment
{
    /// <summary>
    /// Base address of the AniSync backend the thin client talks to
    /// (e.g. "https://anisync.fly.dev/"). On the Web head this is usually the
    /// same origin; on MAUI it's the deployed server URL.
    /// </summary>
    string ApiBaseUrl { get; }

    /// <summary>True when running inside the native MAUI shell (vs. the browser).</summary>
    bool IsNative { get; }

    /// <summary>
    /// Whether this host can play arbitrary codecs natively (LibVLCSharp on
    /// MAUI). Drives whether the watch page uses the native player or falls
    /// back to the in-WebView HTML5 player.
    /// </summary>
    bool SupportsNativePlayback { get; }

    /// <summary>
    /// True when running on a TV (10-foot UI, D-pad remote) — Android TV / Google TV.
    /// Drives the shared UI's TV shell (collapsible left rail + tile-first layout)
    /// instead of the phone/desktop chrome. Native sets it from the device idiom;
    /// the Web head returns false (browser-on-TV is out of scope and undetectable
    /// server-side). Must agree with the JS <c>.tv-mode</c> class that drives the
    /// D-pad focus styling and spatial navigation.
    /// </summary>
    bool IsTv { get; }
}
