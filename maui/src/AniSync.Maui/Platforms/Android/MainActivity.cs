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
            ViewCompat.SetOnApplyWindowInsetsListener(content, new SystemBarInsetsListener());
            ViewCompat.RequestApplyInsets(content);
        }

        // Initial paint follows the system theme; the web app's JS theme bridge takes over once it hydrates.
        AndroidSystemBars.Apply(this, IsSystemDark());
    }

    // UiMode / orientation are in this activity's ConfigChanges, so these land here without a recreate.
    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        AndroidSystemBars.Reapply(this);
    }

    // System back button: navigate back through the in-app (SPA) history like the header's back control,
    // and only fall through to the default (leave/close the app) when there's nothing to go back to — i.e.
    // on the first screen. Blazor's pushState navigations register in the WebView's back-forward list, so
    // CanGoBack/GoBack drive the same history the in-app back button does.
    public override void OnBackPressed()
    {
        var webView = FindWebView(FindViewById(Android.Resource.Id.Content));
        if (webView is not null && webView.CanGoBack())
            webView.GoBack();
        else
            base.OnBackPressed();
    }

    private static Android.Webkit.WebView? FindWebView(AView? view)
    {
        if (view is Android.Webkit.WebView web) return web;
        if (view is Android.Views.ViewGroup group)
        {
            for (var i = 0; i < group.ChildCount; i++)
            {
                var found = FindWebView(group.GetChildAt(i));
                if (found is not null) return found;
            }
        }
        return null;
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
