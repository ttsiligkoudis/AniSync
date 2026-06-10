using LibVLCSharp.Shared;
using LibVLCSharp.MAUI;
using Microsoft.Maui.Controls.Shapes;
#if ANDROID
using AndroidX.Core.View;
#endif

namespace AniSync.Maui;

/// <summary>
/// Full-screen native player page hosting a LibVLCSharp <see cref="VideoView"/> bound to the MediaPlayer
/// that <see cref="VlcMediaPlayer"/> built. Pushed modally over the BlazorWebView shell so the native video
/// surface renders outside the WebView DOM.
///
/// The chrome copies the Stremio mobile player: forced-landscape + immersive, a top bar (back + title), a
/// centre transport (rewind 10s · play/pause · forward 30s), a seek bar with time labels, and a bottom action
/// row opening in-page bottom-sheets for Subtitles, Audio Tracks, Playback Speed and Video scaling — each
/// driving the LibVLC <see cref="MediaPlayer"/> directly (the page already owns it; no player-seam change).
/// Controls auto-hide after a few seconds of inactivity (kept visible while paused or a sheet is open).
/// </summary>
public sealed class VlcPlayerPage : ContentPage
{
    private static readonly Color Accent = Color.FromArgb("#8B5CF6");   // purple seek/selection accent
    private static readonly Color Scrim = Color.FromRgba(0, 0, 0, 150); // gradient-ish bar background
    private static readonly Color SheetBg = Color.FromArgb("#15151B");  // bottom-sheet panel

    private readonly MediaPlayer _player;
    private readonly Slider _seek;
    private readonly Button _playPause;
    private readonly Label _position;
    private readonly Label _duration;
    private readonly Grid _controls;
    private readonly Grid _sheetOverlay;
    private readonly Label _sheetTitle;
    private readonly VerticalStackLayout _sheetItems;
    private readonly ContentView _sheetExtra;
    private readonly IDispatcherTimer _hideTimer;

    private bool _seeking;
    private ScaleMode _scaleMode = ScaleMode.Fit;

    private enum ScaleMode { Fit, SixteenNine, FourThree, Fill }

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

        // ── Top bar: back (‹) + title ───────────────────────────────────────────
        var back = GlyphButton("‹", 30);
        back.Clicked += async (_, _) => await CloseAsync();

        var titleLabel = new Label
        {
            Text = title,
            TextColor = Colors.White,
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            HorizontalOptions = LayoutOptions.Fill,
        };

        var topBar = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) },
            Padding = new Thickness(8, 8, 16, 8),
            BackgroundColor = Scrim,
            VerticalOptions = LayoutOptions.Start,
        };
        topBar.Add(back, 0, 0);
        topBar.Add(titleLabel, 1, 0);

        // ── Centre transport: rewind 10s · play/pause · forward 30s ─────────────
        var rewind = GlyphButton("⏪", 30);
        rewind.Clicked += (_, _) => Nudge(-10_000);

        _playPause = GlyphButton("⏸", 40);
        _playPause.Clicked += (_, _) => TogglePlay();

        var forward = GlyphButton("⏩", 30);
        forward.Clicked += (_, _) => Nudge(+30_000);

        var transport = new HorizontalStackLayout
        {
            Spacing = 28,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { rewind, _playPause, forward },
        };

        // ── Seek row: position · slider · duration ──────────────────────────────
        _position = TimeLabel("0:00");
        _duration = TimeLabel("0:00");

        _seek = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center,
            MinimumTrackColor = Accent,
            ThumbColor = Accent,
            MaximumTrackColor = Color.FromRgba(255, 255, 255, 70),
        };
        _seek.DragStarted += (_, _) => { _seeking = true; StopHideTimer(); };
        _seek.DragCompleted += (_, _) =>
        {
            if (_player.Length > 0) _player.Time = (long)(_seek.Value * _player.Length);
            _seeking = false;
            RestartHideTimer();
        };

        var seekRow = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
            ColumnSpacing = 10,
            Padding = new Thickness(16, 0),
        };
        seekRow.Add(_position, 0, 0);
        seekRow.Add(_seek, 1, 0);
        seekRow.Add(_duration, 2, 0);

        // ── Action row: Subtitles · Audio · Speed · Scaling ─────────────────────
        var actionRow = new Grid
        {
            ColumnDefinitions =
            {
                new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star),
            },
            Padding = new Thickness(8, 4, 8, 12),
        };
        actionRow.Add(ActionButton("💬", "Subtitles", OpenSubtitleSheet), 0, 0);
        actionRow.Add(ActionButton("🔊", "Audio", OpenAudioSheet), 1, 0);
        actionRow.Add(ActionButton("⏱", "Speed", OpenSpeedSheet), 2, 0);
        actionRow.Add(ActionButton("🔳", "Scaling", OpenScalingSheet), 3, 0);

        var bottom = new VerticalStackLayout
        {
            Spacing = 6,
            BackgroundColor = Scrim,
            VerticalOptions = LayoutOptions.End,
            Padding = new Thickness(0, 8, 0, 0),
            Children = { seekRow, actionRow },
        };

        _controls = new Grid
        {
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
        };
        _controls.Add(topBar, 0, 0);
        _controls.Add(transport, 0, 1);
        _controls.Add(bottom, 0, 2);

        // ── Bottom-sheet overlay (hidden until a menu opens) ────────────────────
        _sheetTitle = new Label
        {
            TextColor = Colors.White,
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
        };
        var sheetClose = GlyphButton("✕", 18);
        sheetClose.Clicked += (_, _) => CloseSheet();

        var sheetHeader = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
            Padding = new Thickness(16, 8, 8, 4),
        };
        sheetHeader.Add(_sheetTitle, 0, 0);
        sheetHeader.Add(sheetClose, 1, 0);

        _sheetItems = new VerticalStackLayout { Spacing = 2 };
        _sheetExtra = new ContentView { Padding = new Thickness(16, 4, 16, 0) };

        var sheetBody = new VerticalStackLayout
        {
            Children =
            {
                sheetHeader,
                new ScrollView { Content = _sheetItems, MaximumHeightRequest = 260 },
                _sheetExtra,
            },
        };

        var sheetPanel = new Border
        {
            BackgroundColor = SheetBg,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(18, 18, 0, 0) },
            Padding = new Thickness(0, 4, 0, 16),
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Fill,
            Content = sheetBody,
        };

        var backdrop = new BoxView { Color = Color.FromRgba(0, 0, 0, 120) };
        var backdropTap = new TapGestureRecognizer();
        backdropTap.Tapped += (_, _) => CloseSheet();
        backdrop.GestureRecognizers.Add(backdropTap);

        _sheetOverlay = new Grid { IsVisible = false };
        _sheetOverlay.Add(backdrop);
        _sheetOverlay.Add(sheetPanel);

        // ── Compose: video, then controls, then the sheet on top ───────────────
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => ToggleControls();

        var root = new Grid();
        root.Add(video, 0, 0);
        root.Add(_controls, 0, 0);
        root.Add(_sheetOverlay, 0, 0);
        root.GestureRecognizers.Add(tap);
        Content = root;

        // Auto-hide the chrome after a few seconds of inactivity.
        _hideTimer = Dispatcher.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(3.5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideControls();

        // Keep the UI in sync (libVLC raises these on its own thread → marshal to the UI thread).
        _player.PositionChanged += OnPositionChanged;
        _player.LengthChanged += OnLengthChanged;
        _player.Playing += (_, _) => Dispatcher.Dispatch(() => { _playPause.Text = "⏸"; RestartHideTimer(); });
        _player.Paused += (_, _) => Dispatcher.Dispatch(() => { _playPause.Text = "▶"; StopHideTimer(); });

        // Stop + release when the user backs out of (or closes) the player.
        NavigatedFrom += (_, _) => { try { _player.Stop(); } catch { /* already stopped */ } };
    }

    // ── Lifecycle: force landscape + immersive while open; restore on close ─────
    protected override void OnAppearing()
    {
        base.OnAppearing();
        SetImmersive(true);
        RestartHideTimer();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopHideTimer();
        SetImmersive(false);
    }

    private static void SetImmersive(bool on)
    {
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity is null) return;

        activity.RequestedOrientation = on
            ? Android.Content.PM.ScreenOrientation.SensorLandscape
            : Android.Content.PM.ScreenOrientation.Unspecified;

        var window = activity.Window;
        if (window is null) return;
        var controller = WindowCompat.GetInsetsController(window, window.DecorView);
        if (controller is null) return;

        if (on)
        {
            controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            controller.Hide(WindowInsetsCompat.Type.SystemBars());
        }
        else
        {
            controller.Show(WindowInsetsCompat.Type.SystemBars());
        }
#endif
    }

    // ── Transport ───────────────────────────────────────────────────────────────
    private void TogglePlay()
    {
        try { _player.SetPause(_player.IsPlaying); } catch { /* not ready */ }
        RestartHideTimer();
    }

    private void Nudge(long deltaMs)
    {
        try
        {
            var len = _player.Length;
            var target = _player.Time + deltaMs;
            if (target < 0) target = 0;
            if (len > 0 && target > len) target = len;
            _player.Time = target;
        }
        catch { /* not seekable yet */ }
        RestartHideTimer();
    }

    private async Task CloseAsync()
    {
        try { await Navigation.PopModalAsync(); } catch { /* already closing */ }
    }

    // ── Controls visibility / auto-hide ─────────────────────────────────────────
    private void ToggleControls()
    {
        if (_sheetOverlay.IsVisible) return; // sheet's backdrop owns taps
        if (_controls.IsVisible) HideControls();
        else ShowControls();
    }

    private void ShowControls()
    {
        _controls.IsVisible = true;
        RestartHideTimer();
    }

    private void HideControls()
    {
        // Don't hide while paused or while a menu sheet is open.
        if (_sheetOverlay.IsVisible || !_player.IsPlaying) return;
        _controls.IsVisible = false;
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        if (_player.IsPlaying && !_sheetOverlay.IsVisible)
            _hideTimer.Start();
    }

    private void StopHideTimer() => _hideTimer.Stop();

    // ── Bottom sheets ───────────────────────────────────────────────────────────
    private void OpenSheet(string title, IEnumerable<SheetItem> items, View? extra = null)
    {
        _sheetTitle.Text = title;
        _sheetItems.Clear();
        foreach (var item in items)
            _sheetItems.Add(BuildSheetRow(item));
        _sheetExtra.Content = extra;
        _sheetOverlay.IsVisible = true;
        _controls.IsVisible = true;
        StopHideTimer();
    }

    private void CloseSheet()
    {
        _sheetOverlay.IsVisible = false;
        ShowControls();
    }

    private View BuildSheetRow(SheetItem item)
    {
        var label = new Label
        {
            Text = item.Label,
            TextColor = item.Selected ? Accent : Colors.White,
            FontSize = 15,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var check = new Label
        {
            Text = item.Selected ? "✓" : "",
            TextColor = Accent,
            FontSize = 16,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End,
        };
        var row = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
            Padding = new Thickness(16, 12),
        };
        row.Add(label, 0, 0);
        row.Add(check, 1, 0);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => item.OnTap();
        row.GestureRecognizers.Add(tap);
        return row;
    }

    private void OpenSpeedSheet()
    {
        float current;
        try { current = _player.Rate; } catch { current = 1f; }

        var items = new[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f }.Select(rate =>
            new SheetItem(
                rate == 1.0f ? "Normal (1.0×)" : $"{rate:0.##}×",
                Math.Abs(current - rate) < 0.001f,
                () => { try { _player.SetRate(rate); } catch { } OpenSpeedSheet(); }));

        OpenSheet("Playback Speed", items);
    }

    private void OpenAudioSheet()
    {
        var items = new List<SheetItem>();
        try
        {
            var current = _player.AudioTrack;
            foreach (var t in _player.AudioTrackDescription)
            {
                var id = t.Id;
                items.Add(new SheetItem(
                    string.IsNullOrWhiteSpace(t.Name) ? $"Track {id}" : t.Name,
                    id == current,
                    () => { try { _player.SetAudioTrack(id); } catch { } OpenAudioSheet(); }));
            }
        }
        catch { /* tracks not parsed yet */ }

        if (items.Count == 0)
            items.Add(new SheetItem("No audio tracks", false, () => { }));

        OpenSheet("Audio Tracks", items);
    }

    private void OpenSubtitleSheet()
    {
        var items = new List<SheetItem>();
        var currentSpu = 0;
        try { currentSpu = _player.Spu; } catch { }

        // Explicit "Off" entry (libVLC disables SPU with id -1).
        items.Add(new SheetItem("Off", currentSpu == -1,
            () => { try { _player.SetSpu(-1); } catch { } OpenSubtitleSheet(); }));

        try
        {
            foreach (var t in _player.SpuDescription)
            {
                var id = t.Id;
                if (id == -1) continue; // already shown as "Off"
                items.Add(new SheetItem(
                    string.IsNullOrWhiteSpace(t.Name) ? $"Track {id}" : t.Name,
                    id == currentSpu,
                    () => { try { _player.SetSpu(id); } catch { } OpenSubtitleSheet(); }));
            }
        }
        catch { /* tracks not parsed yet */ }

        OpenSheet("Subtitles", items, BuildSubtitleDelayRow());
    }

    private View BuildSubtitleDelayRow()
    {
        long delayUs;
        try { delayUs = _player.SpuDelay; } catch { delayUs = 0; }

        var value = new Label
        {
            Text = FormatDelay(delayUs),
            TextColor = Colors.White,
            FontSize = 15,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = 80,
            HorizontalTextAlignment = TextAlignment.Center,
        };

        void Adjust(long stepUs)
        {
            try
            {
                var next = _player.SpuDelay + stepUs;
                _player.SetSpuDelay(next);
                value.Text = FormatDelay(next);
            }
            catch { }
        }

        var minus = GlyphButton("−", 22);
        minus.Clicked += (_, _) => Adjust(-100_000); // −0.1s
        var plus = GlyphButton("+", 22);
        plus.Clicked += (_, _) => Adjust(+100_000);   // +0.1s

        var caption = new Label
        {
            Text = "Subtitle delay",
            TextColor = Color.FromRgba(255, 255, 255, 160),
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Start,
        };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
            },
            ColumnSpacing = 4,
            Padding = new Thickness(0, 8, 0, 0),
        };
        row.Add(caption, 0, 0);
        row.Add(minus, 1, 0);
        row.Add(value, 2, 0);
        row.Add(plus, 3, 0);
        return row;
    }

    private void OpenScalingSheet()
    {
        var items = new[]
        {
            new SheetItem("Fit", _scaleMode == ScaleMode.Fit, () => ApplyScale(ScaleMode.Fit)),
            new SheetItem("16:9", _scaleMode == ScaleMode.SixteenNine, () => ApplyScale(ScaleMode.SixteenNine)),
            new SheetItem("4:3", _scaleMode == ScaleMode.FourThree, () => ApplyScale(ScaleMode.FourThree)),
            new SheetItem("Fill", _scaleMode == ScaleMode.Fill, () => ApplyScale(ScaleMode.Fill)),
        };
        OpenSheet("Video scaling", items);
    }

    private void ApplyScale(ScaleMode mode)
    {
        _scaleMode = mode;
        try
        {
            switch (mode)
            {
                case ScaleMode.Fit:
                    _player.AspectRatio = null;
                    _player.Scale = 0;       // auto-fit to the view
                    break;
                case ScaleMode.SixteenNine:
                    _player.AspectRatio = "16:9";
                    _player.Scale = 0;
                    break;
                case ScaleMode.FourThree:
                    _player.AspectRatio = "4:3";
                    _player.Scale = 0;
                    break;
                case ScaleMode.Fill:
                    // Force the display aspect so the picture crops/stretches to fill the screen.
                    var d = DeviceDisplay.Current.MainDisplayInfo;
                    var w = (int)Math.Max(d.Width, d.Height);
                    var h = (int)Math.Min(d.Width, d.Height);
                    _player.AspectRatio = h > 0 ? $"{w}:{h}" : null;
                    _player.Scale = 0;
                    break;
            }
        }
        catch { /* not ready */ }
        OpenScalingSheet();
    }

    // ── Event marshalling + formatting ──────────────────────────────────────────
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

    private static string FormatDelay(long microseconds)
    {
        var seconds = microseconds / 1_000_000.0;
        return $"{seconds:+0.0;-0.0;0.0}s";
    }

    // ── Small UI factories ──────────────────────────────────────────────────────
    private static Button GlyphButton(string glyph, double fontSize) => new()
    {
        Text = glyph,
        FontSize = fontSize,
        TextColor = Colors.White,
        BackgroundColor = Colors.Transparent,
        BorderWidth = 0,
        Padding = new Thickness(0),
        MinimumWidthRequest = 48,
        MinimumHeightRequest = 48,
    };

    private static Label TimeLabel(string text) => new()
    {
        Text = text,
        TextColor = Colors.White,
        FontSize = 12,
        VerticalOptions = LayoutOptions.Center,
    };

    private View ActionButton(string glyph, string label, Action onTap)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 2,
            HorizontalOptions = LayoutOptions.Center,
            Padding = new Thickness(4, 6),
            Children =
            {
                new Label { Text = glyph, FontSize = 18, TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center },
                new Label { Text = label, FontSize = 11, TextColor = Color.FromRgba(255, 255, 255, 200), HorizontalOptions = LayoutOptions.Center },
            },
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        stack.GestureRecognizers.Add(tap);
        return stack;
    }

    private sealed record SheetItem(string Label, bool Selected, Action OnTap);
}
