using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;
using AView = Android.Views.View;

namespace AniSync;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // App chrome background, matching the web app's --bg token for each theme so the inset strips behind
    // the edge-to-edge status/nav bars blend with the header instead of showing a stray band.
    private const string DarkBg = "#0A0A0A";
    private const string LightBg = "#FFFFFF";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Android 15 (API 35) enforces edge-to-edge, so the BlazorWebView draws under the status and
        // navigation bars. The WebView does NOT reliably surface env(safe-area-inset-*) to CSS, so the
        // header rendered behind — and untappable under — the status bar. Pad the content root by the
        // system-bar insets so the whole webview sits inside the safe area; this works across API levels
        // (insets are 0 pre-35 where the system already insets us) and keeps the CSS env() values at 0 so
        // there's no double inset. iOS still relies on the env() padding in site.css.
        var content = FindViewById(Android.Resource.Id.Content);
        if (content is not null)
        {
            ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarInsetsListener());
            ViewCompat.RequestApplyInsets(content);
        }

        ApplyChromeTheme();
    }

    // UiMode is in this activity's ConfigChanges, so a system light/dark switch lands here (no recreate) —
    // re-tint the bars + inset strips to match.
    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyChromeTheme();
    }

    // Paint the inset strips behind the status/nav bars to the app's --bg for the resolved theme, and set
    // the bar icon contrast to match, so the system icons stay visible and the gap blends with the chrome.
    // The webview follows prefers-color-scheme, so the system UI mode is the right signal here. (A manual
    // in-app theme toggle that overrides the OS won't re-tint the native bars until relaunch — acceptable;
    // the common case is system-driven, and this fixes the "dark icons invisible on a dark gap" bug.)
    private void ApplyChromeTheme()
    {
        var content = FindViewById(Android.Resource.Id.Content);
        if (content is null || Window is null) return;

        var night = (Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask)
                    == Android.Content.Res.UiMode.NightYes;

        content.SetBackgroundColor(Android.Graphics.Color.ParseColor(night ? DarkBg : LightBg));

        var controller = WindowCompat.GetInsetsController(Window, content);
        if (controller is not null)
        {
            // "Light bars" == dark icons (for a light background). So day → dark icons, night → light icons.
            controller.AppearanceLightStatusBars = !night;
            controller.AppearanceLightNavigationBars = !night;
        }
    }

    // Pads the host view by the status + navigation bar insets so the BlazorWebView never underlaps them.
    private sealed class SystemBarInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(AView? v, WindowInsetsCompat? insets)
        {
            if (v is not null && insets is not null)
            {
                var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
                v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            }
            return insets!;
        }
    }
}
