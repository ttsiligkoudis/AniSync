using Android.App;
using Android.Content;
using Android.Runtime;
using AndroidX.AppCompat.App;

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
        // Diagnostic logging for unhandled exceptions. NOTE: we deliberately do NOT swallow them
        // (no e.Handled = true): swallowing an unhandled UI-thread exception leaves the app in a broken,
        // wedged state (and, for playback, kept the audio running with a frozen screen — worse than a clean
        // crash). Graceful, user-facing recovery is done at operation boundaries with targeted try/catch
        // (e.g. the Watch page's playback fallback), where the app can stay in a VALID state.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"[AniSync] Unhandled (AppDomain): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[AniSync] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"[AniSync] Unhandled (Android): {e.Exception}");

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
}
