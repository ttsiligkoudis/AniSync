namespace AniSync.Client.Services;

/// <summary>
/// App diagnostic breadcrumb log. The shared UI records noteworthy outcomes (e.g. the
/// mark-watched result) here so an issue that isn't a crash can still be diagnosed after
/// the fact. The native head persists it to a file the user exports via the OS share sheet —
/// scoped storage makes the on-disk path unreachable from a file manager on modern Android,
/// so sharing is the only practical way off the device. The web head writes to the browser
/// console (DevTools), where it's already visible.
/// </summary>
public interface IDiagnosticLog
{
    /// <summary>Append one timestamped breadcrumb. Never throws — callers can log freely.</summary>
    void Log(string category, string message);

    /// <summary>Current log contents (oldest first). Empty when there's nothing logged / unsupported.</summary>
    Task<string> ReadAsync();

    /// <summary>Hand the log to the OS share sheet. Returns false when the head can't share.</summary>
    Task<bool> ShareAsync();

    /// <summary>Wipe the log.</summary>
    Task ClearAsync();

    /// <summary>Whether this head can export the log — drives the Advanced page's diagnostics UI.</summary>
    bool CanShare { get; }
}
