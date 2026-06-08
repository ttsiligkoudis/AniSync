using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

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
            // Match the strip behind the (edge-to-edge) status/nav bars to the app's dark chrome so the
            // padded area isn't a bright band — SetStatusBarColor is deprecated/ignored on API 35.
            content.SetBackgroundColor(Android.Graphics.Color.ParseColor("#0b0d12"));
            ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarInsetsListener());
            ViewCompat.RequestApplyInsets(content);
        }
    }

    // Pads the host view by the status + navigation bar insets so the BlazorWebView never underlaps them.
    private sealed class SystemBarInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return insets;
        }
    }
}
