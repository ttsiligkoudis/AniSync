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
}
