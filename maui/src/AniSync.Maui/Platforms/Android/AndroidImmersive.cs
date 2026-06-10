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
            HideBars(window);
            // The player page is presented modally, and since .NET 9 MAUI hosts modals in a DialogFragment
            // with its OWN window — none of the activity-window treatment above reaches it. Give the dialog
            // window the exact same treatment (cutout opt-in included, or the video surface is inset on the
            // notch side: off-centre Fit, Crop/Fill stopping at the notch edge).
            if (TopModalWindow(activity) is { } modal)
            {
                SetCutout(modal, intoCutout: true);
                HideBars(modal);
            }
        });
    }

    // Same as Apply but resolved from the player page's own platform view (called on Loaded / OnAppearing,
    // when MainActivity isn't in the loop). Walks the context chain back to the activity, then treats the
    // top-most modal dialog window; falls back to the view-based insets controller if none is found.
    public static void ApplyToView(global::Android.Views.View? view)
    {
        if (!Active || view is null) return;
        view.Post(() =>
        {
            if (!Active) return;
            var modal = FindActivity(view.Context) is { } activity ? TopModalWindow(activity) : null;
            if (modal is not null)
            {
                SetCutout(modal, intoCutout: true);
                HideBars(modal);
                return;
            }
#pragma warning disable CA1422
            (view.RootView ?? view).SystemUiVisibility = (StatusBarVisibility)(
                SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky);
#pragma warning restore CA1422
            var controller = ViewCompat.GetWindowInsetsController(view);
            if (controller is null) return;
            controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            controller.Hide(WindowInsetsCompat.Type.SystemBars());
        });
    }

    // HyperOS/MIUI keep showing the status bar with WindowInsetsController.Hide alone even on Android 15.
    // The legacy FULLSCREEN window flag is still honoured there and reliably hides it, so set both. The
    // legacy immersive-sticky SystemUiVisibility flags cover the navigation bar / gesture pill on OEM skins
    // that ignore the modern controller.
    private static void HideBars(global::Android.Views.Window window)
    {
#pragma warning disable CA1422
        window.AddFlags(WindowManagerFlags.Fullscreen);
        window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
            SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen |
            SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky);
#pragma warning restore CA1422
        var controller = WindowCompat.GetInsetsController(window, window.DecorView);
        if (controller is null) return;
        controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
        controller.Hide(WindowInsetsCompat.Type.SystemBars());
    }

    // The window of the top-most modal page: since .NET 9, MAUI shows modals as DialogFragments on the
    // activity's fragment manager, so the last one with a live dialog window is the page on screen.
    private static global::Android.Views.Window? TopModalWindow(Activity activity)
    {
        if (activity is not AndroidX.AppCompat.App.AppCompatActivity ac) return null;
        global::Android.Views.Window? top = null;
        try
        {
            foreach (var f in ac.SupportFragmentManager.Fragments)
            {
                if (f is AndroidX.Fragment.App.DialogFragment df && df.Dialog?.Window is { } w)
                    top = w;
                if (!f.IsAdded) continue;
                foreach (var cf in f.ChildFragmentManager.Fragments)
                    if (cf is AndroidX.Fragment.App.DialogFragment cdf && cdf.Dialog?.Window is { } cw)
                        top = cw;
            }
        }
        catch { /* fragment manager mid-transaction — caller falls back to the view path */ }
        return top;
    }

    private static Activity? FindActivity(global::Android.Content.Context? context)
    {
        while (context is global::Android.Content.ContextWrapper wrapper)
        {
            if (wrapper is Activity activity) return activity;
            context = wrapper.BaseContext;
        }
        return null;
    }

    private static void SetCutout(Activity activity, bool intoCutout)
        => SetCutout(activity.Window, intoCutout);

    private static void SetCutout(global::Android.Views.Window? window, bool intoCutout)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.P) return;
        var attrs = window?.Attributes;
        if (attrs is null) return;
        attrs.LayoutInDisplayCutoutMode = intoCutout
            ? LayoutInDisplayCutoutMode.ShortEdges
            : LayoutInDisplayCutoutMode.Default;
        window!.Attributes = attrs;
    }
}
