using System.Diagnostics;
using AniSync.Client.Services;

namespace AniSync.Web;

/// <summary>
/// Web head's <see cref="IDiagnosticLog"/>. Breadcrumbs go to <see cref="Debug.WriteLine"/> (browser
/// console under WebAssembly / server log under Blazor Server) — DevTools already gives the user a way
/// to see them, so there's no on-device file to export and <see cref="CanShare"/> is false.
/// </summary>
public sealed class WebDiagnosticLog : IDiagnosticLog
{
    public bool CanShare => false;

    public void Log(string category, string message) => Debug.WriteLine($"[AniSync:{category}] {message}");

    public Task<string> ReadAsync() => Task.FromResult("");

    public Task<bool> ShareAsync() => Task.FromResult(false);

    public Task ClearAsync() => Task.CompletedTask;
}
