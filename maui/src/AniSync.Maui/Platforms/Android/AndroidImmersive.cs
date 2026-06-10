using Android.App;
using Android.OS;
using Android.Runtime;
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
#pragma warning disable CA1422 // FLAG_FULLSCREEN deprecated, but still honoured (and needed on MIUI/HyperOS)
        window.ClearFlags(WindowManagerFlags.Fullscreen);
        window.DecorView.SystemUiVisibility = (StatusBarVisibility)SystemUiFlags.Visible;
#pragma warning restore CA1422
        WindowCompat.GetInsetsController(window, window.DecorView)
            ?.Show(WindowInsetsCompat.Type.SystemBars());
    }

    // Re-assert the hidden bars (called on resume / config change while the player is open). Posted to the
    // decor view so it runs AFTER the current layout/transition pass — hiding the bars mid-rotation or
    // mid-modal-present doesn't stick on some OEMs, leaving the status bar visible.
    public static void Apply(Activity activity)
    {
        if (!Active) return;
        var decor = activity.Window?.DecorView;
        if (decor is null) return;
        decor.Post(() =>
        {
            if (!Active) return;
            var window = activity.Window;
            if (window is null) return;
            // HyperOS/MIUI keep showing the status bar with WindowInsetsController.Hide alone even on Android
            // 15. The legacy FULLSCREEN window flag is still honoured there and reliably hides it, so set both.
#pragma warning disable CA1422
            window.AddFlags(WindowManagerFlags.Fullscreen);
            // Belt-and-suspenders for the navigation bar / gesture pill: the legacy immersive-sticky
            // SystemUiVisibility flags, which some OEM skins honour when the modern controller doesn't.
            window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky);
#pragma warning restore CA1422
            var controller = WindowCompat.GetInsetsController(window, window.DecorView);
            if (controller is null) return;
            controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            controller.Hide(WindowInsetsCompat.Type.SystemBars());
        });
    }

    // Target the window that actually HOSTS a given view. MAUI presents modal pages in their own hosting
    // window, so flags set on the Activity window (Apply, above) never reach the modal — but
    // ViewCompat.GetWindowInsetsController resolves the controller for the view's attached window, and setting
    // SystemUiVisibility on that window's decor (view.RootView) covers the legacy path. This is what makes the
    // player modal go truly fullscreen.
    public static void ApplyToView(global::Android.Views.View? view)
    {
        if (!Active || view is null) return;
        view.Post(() =>
        {
            if (!Active) return;
            var root = view.RootView ?? view;
            // The modal window also needs its own cutout opt-in (SetCutout only reaches the Activity window),
            // otherwise the video surface is inset on the notch side and the picture sits off-centre. The
            // decor view of a window carries WindowManager.LayoutParams, so patch them in place.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P &&
                root.LayoutParameters is WindowManagerLayoutParams wlp &&
                wlp.LayoutInDisplayCutoutMode != LayoutInDisplayCutoutMode.ShortEdges)
            {
                try
                {
                    wlp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                    var svc = view.Context?.GetSystemService(global::Android.Content.Context.WindowService);
                    svc?.JavaCast<IWindowManager>()?.UpdateViewLayout(root, wlp);
                }
                catch { /* not a window decor, or update rejected — keep the bars fix regardless */ }
            }
#pragma warning disable CA1422
            root.SystemUiVisibility = (StatusBarVisibility)(
                SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky);
#pragma warning restore CA1422
            var controller = ViewCompat.GetWindowInsetsController(view);
            if (controller is null) return;
            controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            controller.Hide(WindowInsetsCompat.Type.SystemBars());
        });
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
