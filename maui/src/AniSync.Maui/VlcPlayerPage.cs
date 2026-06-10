using LibVLCSharp.Shared;
using LibVLCSharp.MAUI;

namespace AniSync.Maui;

/// <summary>
/// Full-screen native player page hosting a LibVLCSharp <see cref="VideoView"/> bound to the MediaPlayer
/// that <see cref="VlcMediaPlayer"/> built, with a minimal control overlay (close, play/pause, seek). Pushed
/// modally over the BlazorWebView shell so the native video surface renders outside the WebView DOM.
/// </summary>
public sealed class VlcPlayerPage : ContentPage
{
    private readonly MediaPlayer _player;
    private readonly Slider _seek;
    private readonly Button _playPause;
    private readonly Label _position;
    private readonly Label _duration;
    private readonly View _controls;
    private bool _seeking;

    public VlcPlayerPage(MediaPlayer player, string title)
    {
        _player = player;
        Title = title;
        BackgroundColor = Colors.Black;
        NavigationPage.SetHasNavigationBar(this, false);

        var video = new VideoView
        {
            MediaPlayer = player,
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
        };

        // ── Top bar: close (✕) + title ──────────────────────────────────────────
        var close = new Button
        {
            Text = "✕",
            FontSize = 20,
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 48,
            HeightRequest = 48,
        };
        close.Clicked += async (_, _) => await CloseAsync();

        var titleLabel = new Label
        {
            Text = title,
            TextColor = Colors.White,
            FontSize = 16,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            HorizontalOptions = LayoutOptions.Fill,
        };

        var topBar = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) },
            Padding = new Thickness(8, 8, 16, 8),
            BackgroundColor = Color.FromRgba(0, 0, 0, 140),
            VerticalOptions = LayoutOptions.Start,
        };
        topBar.Add(close, 0, 0);
        topBar.Add(titleLabel, 1, 0);

        // ── Bottom bar: play/pause + position + seek + duration ─────────────────
        _playPause = new Button
        {
            Text = "⏸",
            FontSize = 22,
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 48,
            HeightRequest = 48,
        };
        _playPause.Clicked += (_, _) => TogglePlay();

        _position = new Label { Text = "0:00", TextColor = Colors.White, FontSize = 12, VerticalOptions = LayoutOptions.Center };
        _duration = new Label { Text = "0:00", TextColor = Colors.White, FontSize = 12, VerticalOptions = LayoutOptions.Center };

        _seek = new Slider { Minimum = 0, Maximum = 1, Value = 0, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Center };
        _seek.DragStarted += (_, _) => _seeking = true;
        _seek.DragCompleted += (_, _) =>
        {
            if (_player.Length > 0) _player.Time = (long)(_seek.Value * _player.Length);
            _seeking = false;
        };

        var bottomBar = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
            ColumnSpacing = 8,
            Padding = new Thickness(8, 8, 16, 12),
            BackgroundColor = Color.FromRgba(0, 0, 0, 140),
            VerticalOptions = LayoutOptions.End,
        };
        bottomBar.Add(_playPause, 0, 0);
        bottomBar.Add(_position, 1, 0);
        bottomBar.Add(_seek, 2, 0);
        bottomBar.Add(_duration, 3, 0);

        _controls = new Grid
        {
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
            InputTransparent = false,
        };
        ((Grid)_controls).Add(topBar, 0, 0);
        ((Grid)_controls).Add(bottomBar, 0, 2);

        // Tap the video to show/hide the controls.
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => _controls.IsVisible = !_controls.IsVisible;

        var root = new Grid();
        root.Add(video, 0, 0);
        root.Add(_controls, 0, 0);
        root.GestureRecognizers.Add(tap);
        Content = root;

        // Keep the UI in sync (libVLC raises these on its own thread → marshal to the UI thread).
        _player.PositionChanged += OnPositionChanged;
        _player.LengthChanged += OnLengthChanged;
        _player.Playing += (_, _) => Dispatcher.Dispatch(() => _playPause.Text = "⏸");
        _player.Paused += (_, _) => Dispatcher.Dispatch(() => _playPause.Text = "▶");

        // Stop + release when the user backs out of (or closes) the player.
        NavigatedFrom += (_, _) => { try { _player.Stop(); } catch { /* already stopped */ } };
    }

    private void TogglePlay()
    {
        try { _player.SetPause(_player.IsPlaying); } catch { /* not ready */ }
    }

    private async Task CloseAsync()
    {
        try { await Navigation.PopModalAsync(); } catch { /* already closing */ }
    }

    private void OnPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e)
        => Dispatcher.Dispatch(() => { if (!_seeking) _seek.Value = Math.Clamp(e.Position, 0, 1); UpdateTimes(); });

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        => Dispatcher.Dispatch(UpdateTimes);

    private void UpdateTimes()
    {
        _position.Text = Format(_player.Time);
        _duration.Text = Format(_player.Length);
    }

    private static string Format(long ms)
    {
        if (ms <= 0) return "0:00";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
