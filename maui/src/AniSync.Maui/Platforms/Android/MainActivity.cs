using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity;
using AndroidX.Core.View;
using AView = Android.Views.View;

namespace AniSync;

[Activity(Theme = "@style/AniSync.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Draw edge-to-edge on every Android version (API 35 already forces it). This makes the inset
        // handling consistent: without it, versions that still auto-inset content below the status bar PLUS
        // our SystemBarInsetsListener padding produced a DOUBLE gap under the bar (seen on a real device,
        // not the API-35 emulator). With the system no longer insetting us, our listener is the single
        // source of the inset.
        if (Window is not null)
            WindowCompat.SetDecorFitsSystemWindows(Window, false);

        // API 35 enforces edge-to-edge, so the BlazorWebView draws under the status/navigation bars and the
        // WebView doesn't surface env(safe-area-inset-*) to CSS. Pad the content root by the system-bar
        // insets so the header/bottom-nav sit inside the safe area.
        var content = FindViewById(Android.Resource.Id.Content);
        if (content is not null)
        {
            ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarInsetsListener(this));
            ViewCompat.RequestApplyInsets(content);
        }

        // Initial paint follows the system theme; the web app's JS theme bridge takes over once it hydrates.
        AndroidSystemBars.Apply(this, IsSystemDark());

        // Hardware/gesture back → in-app history. Registered on the AndroidX dispatcher (not the deprecated
        // OnBackPressed override) so it ALSO fires under Android 13+ predictive back, which bypasses
        // OnBackPressed — the likely reason back was closing the app outright.
        OnBackPressedDispatcher.AddCallback(this, new WebViewBackCallback(this));
    }

    // UiMode / orientation are in this activity's ConfigChanges, so these land here without a recreate.
    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        AndroidSystemBars.Reapply(this);
    }

    // Re-assert the themed surface colours on resume so a backgrounded → foregrounded transition doesn't
    // flash the WebView's default white before it repaints.
    protected override void OnResume()
    {
        base.OnResume();
        AndroidSystemBars.Reapply(this);
    }

    // Back button → navigate back through the in-app (SPA) history like the header's back control, falling
    // through to the system default (leave the app) only on the first screen. Blazor's pushState
    // navigations register in the WebView's back-forward list, so CanGoBack/GoBack drive the same history
    // the in-app back button does.
    private sealed class WebViewBackCallback : OnBackPressedCallback
    {
        private readonly MainActivity _activity;
        public WebViewBackCallback(MainActivity activity) : base(true) => _activity = activity;

        public override void HandleOnBackPressed()
        {
            var webView = AndroidSystemBars.FindWebView(_activity.FindViewById(Android.Resource.Id.Content));
            if (webView is not null && webView.CanGoBack())
            {
                webView.GoBack();
                return;
            }
            // Nothing to go back to → disable this handler and let the dispatcher run the default (exit).
            Enabled = false;
            _activity.OnBackPressedDispatcher.OnBackPressed();
        }
    }

    private bool IsSystemDark()
        => (Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask)
           == Android.Content.Res.UiMode.NightYes;

    // Pads the host view by the status + navigation bar insets so the BlazorWebView never underlaps them,
    // and re-asserts the themed surface colours — this fires once the WebView is attached, so it's where
    // the WebView background actually gets painted (it may not exist yet in OnCreate).
    private sealed class SystemBarInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        private readonly Activity _activity;
        public SystemBarInsetsListener(Activity activity) => _activity = activity;

        public WindowInsetsCompat OnApplyWindowInsets(AView? v, WindowInsetsCompat? insets)
        {
            if (v is not null && insets is not null)
            {
                var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
                v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            }
            AndroidSystemBars.Reapply(_activity);
            return insets!;
        }
    }
}
