using Android.App;
using Android.Content;
using Android.Runtime;
using AndroidX.AppCompat.App;
using AniSync.Maui;

namespace AniSync;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        // Diagnostic logging for unhandled exceptions. These are persisted to a file (CrashLog) as well as
        // the debugger output, because the user hits unexpected closes on a release build with no debugger
        // attached — Debug.WriteLine is gone by then, but the file survives the process death for later
        // diagnosis. NOTE: we deliberately do NOT swallow them (no e.Handled = true): swallowing an
        // unhandled UI-thread exception leaves the app in a broken, wedged state (and, for playback, kept
        // the audio running with a frozen screen — worse than a clean crash). Graceful, user-facing recovery
        // is done at operation boundaries with targeted try/catch (e.g. the Watch page's playback fallback),
        // where the app can stay in a VALID state.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            CrashLog.Write("AppDomain", e.ExceptionObject);
            System.Diagnostics.Debug.WriteLine($"[AniSync] Unhandled (AppDomain): {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("UnobservedTask", e.Exception);
            System.Diagnostics.Debug.WriteLine($"[AniSync] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            CrashLog.Write("Android", e.Exception);
            System.Diagnostics.Debug.WriteLine($"[AniSync] Unhandled (Android): {e.Exception}");
        };
        // Java-side uncaught exceptions (Media3/ExoPlayer and other native libraries run on Java threads,
        // out of reach of the managed handlers above). Log, then delegate to the previous handler so the OS
        // still records and terminates the crash as normal — we only observe, we don't suppress.
        var previousJavaHandler = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
        Java.Lang.Thread.DefaultUncaughtExceptionHandler = new JavaCrashHandler(previousJavaHandler);

        // Apply the user's saved in-app theme as the app's NATIVE night mode before any activity/window is
        // created, so the values/ vs values-night/ resources (themed windowBackground + splash) follow the
        // in-app light/dark choice rather than the system theme. Saved by AndroidPlatformChrome; first run
        // follows the system.
        try
        {
            using var prefs = GetSharedPreferences("anisync", FileCreationMode.Private);
            AppCompatDelegate.DefaultNightMode =
                prefs?.GetInt("night_mode", AppCompatDelegate.ModeNightFollowSystem)
                ?? AppCompatDelegate.ModeNightFollowSystem;
        }
        catch { /* fall back to the framework default (follow system) */ }

        base.OnCreate();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    // Persists Java-thread uncaught exceptions to the crash log, then chains to the platform's original
    // handler so the crash still propagates (process terminates, OS records it) — observe, don't swallow.
    private sealed class JavaCrashHandler : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _next;
        public JavaCrashHandler(Java.Lang.Thread.IUncaughtExceptionHandler? next) => _next = next;

        public void UncaughtException(Java.Lang.Thread t, Java.Lang.Throwable e)
        {
            CrashLog.Write($"JavaUncaught:{t.Name}", e.ToString());
            _next?.UncaughtException(t, e);
        }
    }
}
