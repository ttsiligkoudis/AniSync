using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity;
using AndroidX.Core.View;

namespace AniSync;

[Activity(Theme = "@style/AniSync.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Also surface the app on the Android TV / Google TV home screen (the leanback launcher), alongside the
// phone/tablet launcher entry that MainLauncher=true provides.
[IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { "android.intent.category.LEANBACK_LAUNCHER" })]
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

        // Edge-to-edge: the WebView fills behind the status/navigation bars, and the web CSS
        // (env(safe-area-inset-*) on the header + bottom nav) provides the inset. We deliberately do NOT
        // also pad the content view natively — the Poco F7's WebView reports env() too, so padding here
        // produced a DOUBLE gap under the status bar. CSS env() is the single source of the inset.

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
        // A forced rotation into landscape clears the immersive flags, so re-hide the bars if a player is open.
        AndroidImmersive.Apply(this);
    }

    // Re-assert the themed surface colours on resume so a backgrounded → foregrounded transition doesn't
    // flash the WebView's default white before it repaints.
    protected override void OnResume()
    {
        base.OnResume();
        AndroidSystemBars.Reapply(this);
        AndroidImmersive.Apply(this);
    }

    // THE place to (re)assert immersive: HyperOS/MIUI (and stock Android) re-show the system bars whenever the
    // window gains focus — presenting the player modal, finishing the forced rotation, or returning from
    // background. Re-hiding here is what makes immersive actually stick.
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) AndroidImmersive.Apply(this);
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

    // While the native TV player is foreground, route remote keys to it: when the player's chrome has
    // auto-hidden, the first D-pad/OK/media press just re-summons it (and is swallowed) — otherwise a
    // remote can't bring hidden controls back. Any other time the event passes through normally.
    public override bool DispatchKeyEvent(Android.Views.KeyEvent? e)
    {
        if (e is not null && e.Action == Android.Views.KeyEventActions.Down && IsWakeKey(e.KeyCode))
        {
            var hook = AniSync.Maui.VlcPlayerPage.TvWakeOnKey;
            if (hook is not null && hook()) return true; // chrome was hidden → consumed the re-summon press
        }
        return base.DispatchKeyEvent(e);
    }

    // D-pad directions, OK/Enter and the media play/pause keys "wake" hidden player chrome. Back and
    // volume are deliberately excluded so they keep their normal behaviour even while chrome is hidden.
    private static bool IsWakeKey(Android.Views.Keycode k) => k is
        Android.Views.Keycode.DpadUp or Android.Views.Keycode.DpadDown or
        Android.Views.Keycode.DpadLeft or Android.Views.Keycode.DpadRight or
        Android.Views.Keycode.DpadCenter or Android.Views.Keycode.Enter or
        Android.Views.Keycode.NumpadEnter or Android.Views.Keycode.MediaPlayPause or
        Android.Views.Keycode.MediaPlay or Android.Views.Keycode.MediaPause;

    private bool IsSystemDark()
        => (Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask)
           == Android.Content.Res.UiMode.NightYes;
}
