using Android.App;
using Android.Content;
using AndroidX.AppCompat.App;
using AniSync.Client.Services;
using Microsoft.Maui.ApplicationModel;

namespace AniSync;

// Android IPlatformChrome: re-tints the OS system bars when the web app's theme bridge reports a change, and
// persists the choice as the native night mode so the next launch's window/splash frame matches it.
public sealed class AndroidPlatformChrome : IPlatformChrome
{
    public void SetTheme(bool dark)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Platform.CurrentActivity is not Activity activity) return;

            // Retint this session's surfaces immediately.
            AndroidSystemBars.Apply(activity, dark);

            // Persist as the native night mode so the NEXT launch resolves values-night to the IN-APP theme
            // (fixing the launch/resume window-background frame + splash). We deliberately DON'T switch
            // AppCompatDelegate.DefaultNightMode live here — that recreates the activity and reloads the
            // webview on every theme toggle. MainApplication.OnCreate applies it at startup instead.
            try
            {
                using var prefs = activity.GetSharedPreferences("anisync", FileCreationMode.Private);
                prefs?.Edit()
                     ?.PutInt("night_mode", dark ? AppCompatDelegate.ModeNightYes : AppCompatDelegate.ModeNightNo)
                     ?.Apply();
            }
            catch { /* best-effort */ }
        });
    }
}
