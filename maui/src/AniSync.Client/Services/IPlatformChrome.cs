namespace AniSync.Client.Services;

/// <summary>
/// Lets the shared UI ask the native host to re-tint the OS system bars (status / navigation) to match the
/// app's current light/dark theme. MAUI Android implements it; the Web head and non-Android MAUI targets
/// use the no-op below — the browser/PWA manages its own chrome via the theme-color meta chrome.js updates.
/// </summary>
public interface IPlatformChrome
{
    /// <summary>Re-tint the OS system bars for a dark (true) or light (false) app theme.</summary>
    void SetTheme(bool dark);
}

public sealed class NoOpPlatformChrome : IPlatformChrome
{
    public void SetTheme(bool dark) { }
}
