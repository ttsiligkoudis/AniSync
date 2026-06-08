using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;
using AView = Android.Views.View;

namespace AniSync;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
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

        // Initial paint follows the system theme so the first frame (before the webview hydrates) isn't
        // wrong; once the web app loads, its JS theme bridge calls IPlatformChrome.SetTheme to reflect the
        // actual in-app theme (including a manual override of the OS).
        AndroidSystemBars.Apply(this, IsSystemDark());
    }

    // UiMode / orientation are in this activity's ConfigChanges, so these land here without a recreate.
    // Re-apply the last theme (not the system one) so a rotation can't revert a manual in-app override.
    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        AndroidSystemBars.Reapply(this);
    }

    private bool IsSystemDark()
        => (Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask)
           == Android.Content.Res.UiMode.NightYes;

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
