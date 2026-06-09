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
        // Apply the user's saved in-app theme as the app's NATIVE night mode before any activity/window is
        // created. This makes values/ vs values-night/ resources (the themed windowBackground, and the
        // splash) resolve to the IN-APP theme rather than the device's system theme — so the launch/resume
        // window frame and the splash follow the app's light/dark choice and never flash the opposite.
        // Saved by AndroidPlatformChrome on every theme change; first run falls back to following the system.
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
