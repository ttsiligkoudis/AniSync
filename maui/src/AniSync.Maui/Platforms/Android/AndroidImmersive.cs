using Android.App;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

namespace AniSync;

// Immersive (hide status + navigation bars) for the native video player, with swipe-to-reveal transient bars
// like YouTube/Netflix/Stremio. Kept as a small stateful helper because hiding the system bars does NOT
// survive a configuration change (the forced rotation to landscape clears it), so MainActivity re-applies it
// on OnConfigurationChanged / OnResume while a player is open. Also opts the window into drawing behind the
// display cutout (notch) on the short edges so a landscape notch doesn't letterbox a black gap.
internal static class AndroidImmersive
{
    public static bool Active { get; private set; }

    public static void Enter(Activity activity)
    {
        Active = true;
        SetCutout(activity, intoCutout: true);
        Apply(activity);
    }

    public static void Exit(Activity activity)
    {
        Active = false;
        SetCutout(activity, intoCutout: false);
        var window = activity.Window;
        if (window is null) return;
        WindowCompat.GetInsetsController(window, window.DecorView)
            ?.Show(WindowInsetsCompat.Type.SystemBars());
    }

    // Re-assert the hidden bars (called on resume / config change while the player is open).
    public static void Apply(Activity activity)
    {
        if (!Active) return;
        var window = activity.Window;
        if (window is null) return;
        var controller = WindowCompat.GetInsetsController(window, window.DecorView);
        if (controller is null) return;
        controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
        controller.Hide(WindowInsetsCompat.Type.SystemBars());
    }

    private static void SetCutout(Activity activity, bool intoCutout)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.P) return;
        var attrs = activity.Window?.Attributes;
        if (attrs is null) return;
        attrs.LayoutInDisplayCutoutMode = intoCutout
            ? LayoutInDisplayCutoutMode.ShortEdges
            : LayoutInDisplayCutoutMode.Default;
        activity.Window!.Attributes = attrs;
    }
}
