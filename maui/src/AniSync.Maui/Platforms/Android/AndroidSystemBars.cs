using Android.App;
using AndroidX.Core.View;
using AView = Android.Views.View;

namespace AniSync;

// Tints the OS status + navigation bars to the app's resolved theme: the inset strips behind the
// edge-to-edge bars are painted with the web app's --bg for that theme, and the system icon contrast is set
// to match (so they never disappear). Shared by MainActivity (initial system-driven paint) and
// AndroidPlatformChrome (the JS theme bridge, which reflects a manual in-app override). Remembers the last
// applied value so a config change (rotation) can re-apply it without reverting to the system theme.
internal static class AndroidSystemBars
{
    // App chrome background, matching the web app's --bg token per theme.
    private const string DarkBg = "#0A0A0A";
    private const string LightBg = "#FFFFFF";

    private static bool _lastDark = true;

    public static void Apply(Activity activity, bool dark)
    {
        _lastDark = dark;

        var window = activity.Window;
        var content = activity.FindViewById(Android.Resource.Id.Content);
        if (window is null || content is null) return;

        var color = Android.Graphics.Color.ParseColor(dark ? DarkBg : LightBg);
        content.SetBackgroundColor(color);
        // The BlazorWebView surface defaults to WHITE, so it flashes white on launch/resume (before the page
        // repaints) — jarring in dark mode. Paint it the theme colour so any pre-paint frame matches.
        FindWebView(content)?.SetBackgroundColor(color);
        // Under Android 15 edge-to-edge the status/nav bars are transparent (SetStatusBarColor is ignored),
        // so the strips behind them show the WINDOW background — which otherwise stays the splash theme's
        // dark even when the app is light. Paint the window background to the theme colour so the strips
        // match. SetStatusBar/NavigationBarColor are kept for pre-35 devices where they still apply.
        window.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(color));
#pragma warning disable CA1422 // deprecated on API 35 (no-op there); still honoured on older devices
        window.SetStatusBarColor(color);
        window.SetNavigationBarColor(color);
#pragma warning restore CA1422

        var controller = WindowCompat.GetInsetsController(window, content);
        if (controller is not null)
        {
            // "Light bars" == dark icons (for a light background): light theme → dark icons, dark → light.
            controller.AppearanceLightStatusBars = !dark;
            controller.AppearanceLightNavigationBars = !dark;
        }
    }

    // Re-apply the last theme (used on config changes that don't recreate the activity, e.g. rotation, and
    // on resume to re-assert the surface colours before a repaint).
    public static void Reapply(Activity activity) => Apply(activity, _lastDark);

    // Depth-first search for the BlazorWebView's underlying Android WebView in the view tree.
    public static Android.Webkit.WebView? FindWebView(AView? view)
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
}
