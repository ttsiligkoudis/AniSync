using Android.App;
using AndroidX.Core.View;

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

        content.SetBackgroundColor(Android.Graphics.Color.ParseColor(dark ? DarkBg : LightBg));

        var controller = WindowCompat.GetInsetsController(window, content);
        if (controller is not null)
        {
            // "Light bars" == dark icons (for a light background): light theme → dark icons, dark → light.
            controller.AppearanceLightStatusBars = !dark;
            controller.AppearanceLightNavigationBars = !dark;
        }
    }

    // Re-apply the last theme (used on config changes that don't recreate the activity, e.g. rotation).
    public static void Reapply(Activity activity) => Apply(activity, _lastDark);
}
