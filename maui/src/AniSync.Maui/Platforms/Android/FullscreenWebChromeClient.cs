using Android.App;
using Android.Content.PM;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AndroidX.Core.View;
// MAUI's global usings also pull in Microsoft.Maui.Controls.View, so the bare name `View` is ambiguous
// in this Android file (CS0104). Everything here is an Android view, so alias it to the Android type.
using View = Android.Views.View;

namespace AniSync;

/// <summary>
/// Gives the Blazor WebView HTML5 / iframe fullscreen support. MAUI's BlazorWebView ships no
/// <see cref="WebChromeClient"/> that handles <see cref="OnShowCustomView"/>, so a YouTube
/// trailer's fullscreen button (and any native &lt;video&gt; fullscreen) was a silent no-op.
///
/// On a fullscreen request Chromium hands us the player's "custom view"; we park it over the
/// Activity's decor view at match-parent, hide the system bars, and rotate to landscape — then
/// undo all three when the player exits fullscreen. Blazor's JS↔.NET bridge runs over the
/// WebView's message channel, not the chrome client, so replacing the chrome client here is safe.
/// No file-chooser override is needed: the app has no &lt;input type="file"&gt;.
/// </summary>
internal sealed class FullscreenWebChromeClient : WebChromeClient
{
    private View? _customView;
    private ICustomViewCallback? _callback;
    private ScreenOrientation _savedOrientation = ScreenOrientation.Unspecified;

    private static Activity? CurrentActivity => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;

    public override void OnShowCustomView(View? view, ICustomViewCallback? callback)
    {
        // A second show without an intervening hide → collapse the first (matches Chromium's contract).
        if (_customView is not null)
        {
            OnHideCustomView();
            return;
        }

        if (view is null || CurrentActivity is not { Window.DecorView: FrameLayout decor } activity)
        {
            callback?.OnCustomViewHidden();
            return;
        }

        _customView = view;
        _callback = callback;
        _savedOrientation = activity.RequestedOrientation;

        _customView.SetBackgroundColor(Android.Graphics.Color.Black);
        decor.AddView(_customView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        activity.RequestedOrientation = ScreenOrientation.SensorLandscape;
        SetSystemBars(activity, visible: false);
    }

    public override void OnHideCustomView()
    {
        if (_customView is null) return;

        if (CurrentActivity is { Window.DecorView: FrameLayout decor } activity)
        {
            decor.RemoveView(_customView);
            activity.RequestedOrientation = _savedOrientation;
            SetSystemBars(activity, visible: true);
        }

        _callback?.OnCustomViewHidden();
        _customView = null;
        _callback = null;
    }

    // WindowInsetsControllerCompat works back to API 21 (the app already uses WindowCompat for its
    // edge-to-edge setup), so no per-API-level branching. We only toggle the bars — the Activity's
    // decorFitsSystemWindows=false (edge-to-edge) baseline is left untouched.
    private static void SetSystemBars(Activity activity, bool visible)
    {
        var window = activity.Window;
        if (window?.DecorView is null) return;
        var controller = WindowCompat.GetInsetsController(window, window.DecorView);
        var bars = WindowInsetsCompat.Type.SystemBars();
        if (visible)
        {
            controller.Show(bars);
        }
        else
        {
            controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            controller.Hide(bars);
        }
    }
}
