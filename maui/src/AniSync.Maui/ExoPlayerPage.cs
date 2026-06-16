using AniSync.Client.Services;
using CommunityToolkit.Maui.Views;

namespace AniSync.Maui;

/// <summary>
/// Experimental alternate player built on the Community Toolkit <see cref="MediaElement"/>, which on Android
/// uses Google's ExoPlayer (Media3) — the engine Stremio defaults to, and the one that "just works" for 4K
/// on Android TV where the embedded libVLC SurfaceView never presented a frame. Kept deliberately minimal
/// for the experiment: full-screen video, ExoPlayer's own transport controls (D-pad friendly), resume, and
/// progress/end forwarded to the request callbacks so resume + scrobble still work. If this plays 4K cleanly
/// we adopt it for TV (or expose it as a player choice in settings).
/// </summary>
public sealed class ExoPlayerPage : ContentPage
{
    private readonly MediaElement _media;
    private readonly PlaybackRequest _request;
    private bool _resumed;

    public ExoPlayerPage(PlaybackRequest request)
    {
        _request = request;
        Title = request.Title;
        BackgroundColor = Colors.Black;
        NavigationPage.SetHasNavigationBar(this, false);

        _media = new MediaElement
        {
            Source = MediaSource.FromUri(request.Url),
            ShouldAutoPlay = true,
            ShouldShowPlaybackControls = true,   // ExoPlayer's native transport (works with the TV remote)
            ShouldKeepScreenOn = true,
            Aspect = Aspect.AspectFit,
            Background = Brush.Black,
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
        };

        // Seek to the resume point once the media is ready (Duration/seek aren't valid before this fires).
        _media.MediaOpened += OnMediaOpened;
        // Read MediaElement.Position directly in the handler so we don't depend on the (version-specific)
        // event-args type name. Drives resume persistence + scrobble like the libVLC player does.
        if (request.OnProgress is not null)
            _media.PositionChanged += (_, _) =>
            {
                try { request.OnProgress(_media.Position.TotalSeconds, _media.Duration.TotalSeconds); }
                catch { /* renderer gone */ }
            };
        if (request.OnEnded is not null)
            _media.MediaEnded += (_, _) => { try { request.OnEnded!(); } catch { /* renderer gone */ } };

        Content = new Grid { BackgroundColor = Colors.Black, Children = { _media } };
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        if (_resumed) return;
        _resumed = true;
        if (_request.ResumeSeconds is > 0)
            try { _ = _media.SeekTo(TimeSpan.FromSeconds(_request.ResumeSeconds.Value)); } catch { /* not seekable */ }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Stop + release ExoPlayer so it doesn't keep decoding/holding the surface after the page closes.
        try { _media.Stop(); } catch { /* already stopped */ }
        try { _media.Handler?.DisconnectHandler(); } catch { /* already torn down */ }
    }
}
