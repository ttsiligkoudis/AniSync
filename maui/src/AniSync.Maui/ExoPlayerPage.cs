using AniSync.Client.Services;

namespace AniSync.Maui;

/// <summary>
/// The Android TV player. Hosts an <see cref="ExoVideoView"/> (a Media3 ExoPlayer + PlayerView) full-screen
/// and immersive. ExoPlayer plays 4K cleanly where the embedded libVLC SurfaceView never presented a frame,
/// and PlayerView's built-in controls give D-pad-friendly play/seek plus a settings menu for audio/subtitle
/// tracks and playback speed; the handler adds sideloaded subtitles, preferred languages, resume,
/// progress/ended callbacks and a MENU-key scaling cycle. Presented modally by <c>VlcMediaPlayer</c> on TV;
/// phones use the in-app libVLC player instead.
/// </summary>
public sealed class ExoPlayerPage : ContentPage
{
    private readonly ExoVideoView _video;
    private Window? _window;

    public ExoPlayerPage(PlaybackRequest request)
    {
        Title = request.Title;
        BackgroundColor = Colors.Black;
        NavigationPage.SetHasNavigationBar(this, false);
        // Draw edge-to-edge under a landscape display cutout (notch) instead of letting MAUI inset
        // the player off the notch side — the modal window is opted into the cutout by AndroidImmersive,
        // but MAUI 10 still applies safe-area insets per page AND per layout, so both must opt out (same
        // fix as VlcPlayerPage). Without this the video shows a black gap on the notch edge.
        SafeAreaEdges = SafeAreaEdges.None;

        _video = new ExoVideoView(request)
        {
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
        };
        Content = new Grid { BackgroundColor = Colors.Black, SafeAreaEdges = SafeAreaEdges.None, Children = { _video } };

        // Re-assert immersive once the page's native view is attached to its (modal) window.
        Loaded += (_, _) => ApplyImmersiveToView();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        SetImmersive(true);
        ApplyImmersiveToView();
        // Hiding the bars doesn't survive the modal-present + forced rotation on some OEMs, so re-assert once
        // the transition settles (MainActivity also re-applies on config change / resume).
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(350), () => { SetImmersive(true); ApplyImmersiveToView(); });

        _window = Application.Current?.Windows.FirstOrDefault();
        if (_window is not null)
        {
            _window.Stopped += OnWindowStopped;
            _window.Resumed += OnWindowResumed;
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_window is not null)
        {
            _window.Stopped -= OnWindowStopped;
            _window.Resumed -= OnWindowResumed;
            _window = null;
        }
        SetImmersive(false);
        // Release ExoPlayer so it stops decoding / holding the surface once the page closes.
        try { _video.Handler?.DisconnectHandler(); } catch { /* already torn down */ }
    }

    // App backgrounded (Home / recents) → pause so audio doesn't keep playing behind an inactive app; resume
    // what was playing when we return.
    private void OnWindowStopped(object? sender, EventArgs e) => _video.PauseForBackground();
    private void OnWindowResumed(object? sender, EventArgs e) => _video.ResumeFromBackground();

    // Target the page's OWN hosting window for immersive: MAUI presents the modal in a separate window, so
    // flags on the Activity window never reach it — this resolves the right window from the view itself.
    private void ApplyImmersiveToView()
    {
#if ANDROID
        if (Handler?.PlatformView is global::Android.Views.View v)
            global::AniSync.AndroidImmersive.ApplyToView(v);
#endif
    }

    private static void SetImmersive(bool on)
    {
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity is null) return;
        if (on)
        {
            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
            global::AniSync.AndroidImmersive.Enter(activity);
        }
        else
        {
            global::AniSync.AndroidImmersive.Exit(activity);
            activity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Unspecified;
        }
#endif
    }
}
