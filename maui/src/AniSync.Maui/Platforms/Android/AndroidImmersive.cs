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
                TreatModalWindow(modal);
        });
    }

    // Dialog-type windows are normally never laid out into the display cutout regardless of cutout mode —
    // LAYOUT_NO_LIMITS is the documented escape hatch that lets a window extend beyond the screen's "safe"
    // limits, cutout included. Combined with a MATCH_PARENT layout this makes the modal truly edge-to-edge.
    //
    // TV EXPERIMENT: a TV has no display cutout, so the cutout opt-in + LAYOUT_NO_LIMITS here do nothing
    // useful there — but they DO reshape the modal window's surface, which is the leading suspect for why
    // MediaCodec direct-rendering of 4K decodes frames yet never presents one on TV (works fine in a plain
    // VLC Activity). So on TV, leave the modal window's surface untouched (just size it full-screen + hide
    // the bars); keep the full notch treatment on phones, where the cutout actually exists.
    private static void TreatModalWindow(global::Android.Views.Window modal)
    {
        if (DeviceInfo.Current.Idiom != DeviceIdiom.TV)
        {
            SetCutout(modal, intoCutout: true);
            modal.AddFlags(WindowManagerFlags.LayoutNoLimits);
        }
        modal.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        HideBars(modal);
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
                TreatModalWindow(modal);
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

    // Compact on-screen diagnostics: which windows we found, their cutout mode/flags, and where the page's
    // view actually sits versus the physical display — to pinpoint WHERE the notch-side inset is applied.
    public static string Describe(global::Android.Views.View? view)
    {
        try
        {
            if (view is null) return "view=null";
            var sb = new System.Text.StringBuilder();
            var activity = FindActivity(view.Context);
            sb.Append(activity is null ? "act:no" : "act:ok");
            var modal = activity is null ? null : TopModalWindow(activity);
            if (modal?.Attributes is { } ma)
                sb.Append($" modal:cut={(int)ma.LayoutInDisplayCutoutMode},fl=0x{(long)ma.Flags:X},{ma.Width}x{ma.Height},g={(int)ma.Gravity}");
            else
                sb.Append(" modal:none");
            var root = view.RootView;
            if (root is not null)
            {
                var rl = new int[2];
                root.GetLocationOnScreen(rl);
                sb.Append($" root:{root.GetType().Name}@{rl[0]},{rl[1]} {root.Width}x{root.Height}");
                if (root.LayoutParameters is WindowManagerLayoutParams wlp)
                    sb.Append($" wlp:cut={(int)wlp.LayoutInDisplayCutoutMode},fl=0x{(long)wlp.Flags:X}");
            }
            var vl = new int[2];
            view.GetLocationOnScreen(vl);
            sb.Append($" view@{vl[0]},{vl[1]} {view.Width}x{view.Height}");
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R && activity?.WindowManager?.CurrentWindowMetrics.Bounds is { } b)
                sb.Append($" disp:{b.Width()}x{b.Height()}");
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P && view.RootWindowInsets?.DisplayCutout is { } dc)
                sb.Append($" dcut:l{dc.SafeInsetLeft},t{dc.SafeInsetTop},r{dc.SafeInsetRight},b{dc.SafeInsetBottom}");
            else
                sb.Append(" dcut:none");
            return sb.ToString();
        }
        catch (System.Exception ex)
        {
            return "dbg-err:" + ex.GetType().Name;
        }
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

    // HDR passthrough: put the player's window(s) into HDR colour mode so an HDR-capable panel actually
    // presents PQ/HLG content (bright/vivid) instead of rendering it dark. The video surface lives in the
    // modal DialogFragment's OWN window (see the cutout note above), so the mode must be set there; we set it
    // on the activity window too and clear that again on teardown so the rest of the app UI isn't left in HDR.
    public static void RequestHdrColorMode(global::Android.Views.View? view)
    {
        if (view is null || Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        view.Post(() =>
        {
            var activity = FindActivity(view.Context);
            if (activity is null) return;
            ApplyColorMode(activity.Window, hdr: true);
            if (TopModalWindow(activity) is { } modal) ApplyColorMode(modal, hdr: true);
        });
    }

    public static void ClearHdrColorMode(global::Android.Views.View? view)
    {
        if (view is null || Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var activity = FindActivity(view.Context);
        if (activity is not null) ApplyColorMode(activity.Window, hdr: false);
    }

    private static void ApplyColorMode(global::Android.Views.Window? window, bool hdr)
    {
        if (window is null || Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        try
        {
            // net-android binds Java setColorMode(int) as the Window.ColorMode property, typed as the
            // ActivityColorMode enum (COLOR_MODE_HDR → Hdr, COLOR_MODE_DEFAULT → Default).
            window.ColorMode = hdr
                ? global::Android.Content.PM.ActivityColorMode.Hdr
                : global::Android.Content.PM.ActivityColorMode.Default;
        }
        catch { /* OEM window without HDR colour-mode support */ }
    }
}
