using System.Text;

namespace AniSync.Maui;

/// <summary>
/// Appends unhandled-exception details to a persistent file so an unexpected close can be diagnosed
/// after the fact. The <see cref="Debug.WriteLine"/> handlers only surface in an attached debugger /
/// logcat, which is gone by the time the user notices the crash; this survives the process death.
///
/// On Android the file lives in EXTERNAL app-specific storage
/// (<c>Android/data/&lt;pkg&gt;/files/crash.log</c>) so it can be pulled via a file manager, USB/MTP,
/// or <c>adb pull</c> without root. Elsewhere it falls back to the app-data directory.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();
    // Keep the file bounded so repeated crashes can't grow it without limit; trim to the newest half.
    private const long MaxBytes = 256 * 1024;

    public static string LogPath
    {
        get
        {
#if ANDROID
            var dir = Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath
                      ?? Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
#else
            var dir = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
#endif
            return Path.Combine(dir, "crash.log");
        }
    }

    /// <summary>Append one timestamped entry. Never throws — it runs from inside crash handlers.</summary>
    public static void Write(string source, object? error)
    {
        try
        {
            var entry = new StringBuilder()
                .Append("===== ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
                .Append("  [").Append(source).Append("] =====").AppendLine()
                .AppendLine(error?.ToString() ?? "(null)")
                .AppendLine()
                .ToString();

            lock (Gate)
            {
                var path = LogPath;
                Trim(path);
                File.AppendAllText(path, entry);
            }
        }
        catch { /* a logger that throws would itself take the process down — swallow */ }
    }

    // When the log passes the cap, keep only the most recent half so the newest crash is always retained.
    private static void Trim(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            var text = File.ReadAllText(path);
            File.WriteAllText(path, text[(text.Length / 2)..]);
        }
        catch { /* best-effort housekeeping */ }
    }
}
