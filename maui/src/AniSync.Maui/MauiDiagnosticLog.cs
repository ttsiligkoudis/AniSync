using AniSync.Client.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace AniSync.Maui;

/// <summary>
/// Native head's <see cref="IDiagnosticLog"/>. Persists breadcrumbs through <see cref="CrashLog"/>
/// (same file as unhandled-crash dumps) and exports them via the OS share sheet — on Android 11+
/// the <c>Android/data/&lt;pkg&gt;/files</c> directory isn't browsable from the Files app, so the
/// share sheet (email / Drive / messaging) is the realistic way to get the log to a developer.
/// </summary>
public sealed class MauiDiagnosticLog : IDiagnosticLog
{
    public bool CanShare => true;

    public void Log(string category, string message) => CrashLog.Write(category, message);

    public Task<string> ReadAsync()
    {
        try
        {
            var path = CrashLog.LogPath;
            return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : "");
        }
        catch { return Task.FromResult(""); }
    }

    public async Task<bool> ShareAsync()
    {
        try
        {
            var path = CrashLog.LogPath;
            // Guarantee the file exists so the share sheet has something to attach even on a fresh
            // install that's never crashed (the common case for a diagnostics export).
            if (!File.Exists(path)) CrashLog.Write("Diagnostics", "Log exported (no prior entries).");
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "AniSync diagnostics",
                File = new ShareFile(path),
            });
            return true;
        }
        catch { return false; }
    }

    public Task ClearAsync()
    {
        try { var path = CrashLog.LogPath; if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }
}
