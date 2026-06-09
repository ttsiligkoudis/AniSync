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
        // Global safety net: surface a generic message and keep the app alive instead of hard-crashing on an
        // unhandled MANAGED exception (any screen). The Android raiser with Handled=true is what actually
        // prevents process death; AppDomain/Task handlers are best-effort logging + toast. NOTE: this can't
        // catch true native crashes (e.g. a libVLC SIGSEGV) — those still terminate the process.
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[AniSync] Unhandled (Android): {e.Exception}");
            ShowErrorToast();
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[AniSync] Unhandled (AppDomain): {e.ExceptionObject}");
            ShowErrorToast();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[AniSync] Unobserved task exception: {e.Exception}");
            ShowErrorToast();
            e.SetObserved();
        };

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

    // Non-blocking generic error message. Best-effort: posts to the UI thread and swallows any failure
    // (e.g. if there's no current context), since this runs from crash handlers.
    private static void ShowErrorToast()
    {
        try
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    Android.Widget.Toast.MakeText(
                        Android.App.Application.Context,
                        "Something went wrong. Please try again.",
                        Android.Widget.ToastLength.Long)?.Show();
                }
                catch { /* no context / toast unavailable */ }
            });
        }
        catch { /* MainThread unavailable */ }
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
