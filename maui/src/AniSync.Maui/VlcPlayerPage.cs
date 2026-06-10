using LibVLCSharp.Shared;
using LibVLCSharp.MAUI;
using Microsoft.Maui.Controls.Shapes;

namespace AniSync.Maui;

/// <summary>
/// Full-screen native player page hosting a LibVLCSharp <see cref="VideoView"/> bound to the MediaPlayer
/// that <see cref="VlcMediaPlayer"/> built. Pushed modally over the BlazorWebView shell so the native video
/// surface renders outside the WebView DOM.
///
/// The chrome copies the Stremio mobile player: forced-landscape + immersive (system bars hidden, swipe to
/// reveal), a compact top bar (back + title), a centre transport (rewind 30s · play/pause · forward 30s), a
/// seek bar with time labels, and a slim bottom action row opening in-page bottom-sheets for Subtitles
/// (Stremio-style language/track/options columns), Audio Tracks, Playback Speed and Video scaling — each
/// driving the LibVLC <see cref="MediaPlayer"/> directly. All control glyphs come from the bundled Material
/// Icons font so they're uniform in size. Controls auto-hide after a few seconds; playback pauses when the
/// app is backgrounded (Home).
/// </summary>
public sealed class VlcPlayerPage : ContentPage
{
    private static readonly Color Accent = Color.FromArgb("#8B5CF6");   // purple seek/selection accent
    private static readonly Color Scrim = Color.FromRgba(0, 0, 0, 150); // bar background
    private static readonly Color SheetBg = Color.FromArgb("#15151B");  // bottom-sheet panel

    // Material Icons codepoints (font registered as "MaterialIcons" in MauiProgram).
    private const string IconFont = "MaterialIcons";
    private const string IcBack = "";      // chevron_left
    private const string IcReplay30 = "";  // replay_30
    private const string IcPlay = "";      // play_arrow
    private const string IcPause = "";     // pause
    private const string IcForward30 = ""; // forward_30
    private const string IcSubtitles = ""; // subtitles
    private const string IcAudio = "";     // volume_up
    private const string IcSpeed = "";     // speed
    private const string IcAspect = "";    // aspect_ratio
    private const string IcClose = "";     // close
    private const string IcRemove = "";    // remove
    private const string IcAdd = "";       // add
    private const string IcCheck = "";     // check

    private readonly MediaPlayer _player;
    private readonly VideoView _videoView;
    private readonly Slider _seek;
    private readonly Button _playPause;
    private readonly Label _position;
    private readonly Label _duration;
    private readonly Grid _controls;
    private readonly Grid _sheetOverlay;
    private readonly Label _sheetTitle;
    private readonly ContentView _sheetContent;
    private readonly IDispatcherTimer _hideTimer;

    private bool _seeking;
    private ScaleMode _scaleMode = ScaleMode.Fit;
    private string? _subLang;                        // selected language column in the subtitle sheet
    private Microsoft.Maui.Controls.Window? _window; // for the background-pause hook

    private enum ScaleMode { Fit, SixteenNine, FourThree, Fill }

    public VlcPlayerPage(MediaPlayer player, string title)
    {
        _player = player;
        Title = title;
        BackgroundColor = Colors.Black;
        NavigationPage.SetHasNavigationBar(this, false);

        _videoView = new VideoView
        {
            MediaPlayer = player,
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
        };

        // ── Top bar (compact): back + title ─────────────────────────────────────
        var back = GlyphButton(IcBack, 28, 44);
        back.Clicked += async (_, _) => await CloseAsync();

        var titleLabel = new Label
        {
            Text = title,
            TextColor = Colors.White,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            HorizontalOptions = LayoutOptions.Fill,
        };

        var topBar = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) },
            Padding = new Thickness(4, 0, 12, 0),
            BackgroundColor = Scrim,
            VerticalOptions = LayoutOptions.Start,
        };
        topBar.Add(back, 0, 0);
        topBar.Add(titleLabel, 1, 0);

        // ── Centre transport: rewind 30s · play/pause · forward 30s (uniform size) ─
        var rewind = GlyphButton(IcReplay30, 32, 60);
        rewind.Clicked += (_, _) => Nudge(-30_000);

        _playPause = GlyphButton(IcPause, 32, 60);
        _playPause.Clicked += (_, _) => TogglePlay();

        var forward = GlyphButton(IcForward30, 32, 60);
        forward.Clicked += (_, _) => Nudge(+30_000);

        var transport = new HorizontalStackLayout
        {
            Spacing = 34,
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
            Padding = new Thickness(14, 0),
        };
        seekRow.Add(_position, 0, 0);
        seekRow.Add(_seek, 1, 0);
        seekRow.Add(_duration, 2, 0);

        // ── Action row (slim, icon + label inline): Subtitles · Audio · Speed · Scaling ─
        var actionRow = new Grid
        {
            ColumnDefinitions =
            {
                new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star),
            },
            Padding = new Thickness(6, 2, 6, 4),
        };
        actionRow.Add(ActionButton(IcSubtitles, "Subtitles", OpenSubtitleSheet), 0, 0);
        actionRow.Add(ActionButton(IcAudio, "Audio", OpenAudioSheet), 1, 0);
        actionRow.Add(ActionButton(IcSpeed, "Speed", OpenSpeedSheet), 2, 0);
        actionRow.Add(ActionButton(IcAspect, "Scaling", OpenScalingSheet), 3, 0);

        var bottom = new VerticalStackLayout
        {
            Spacing = 2,
            BackgroundColor = Scrim,
            VerticalOptions = LayoutOptions.End,
            Padding = new Thickness(0, 4, 0, 4),
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
        var sheetClose = GlyphButton(IcClose, 22, 40);
        sheetClose.Clicked += (_, _) => CloseSheet();

        var sheetHeader = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
            Padding = new Thickness(16, 6, 8, 2),
        };
        sheetHeader.Add(_sheetTitle, 0, 0);
        sheetHeader.Add(sheetClose, 1, 0);

        _sheetContent = new ContentView();

        var sheetBody = new VerticalStackLayout { Children = { sheetHeader, _sheetContent } };

        var sheetPanel = new Border
        {
            BackgroundColor = SheetBg,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(18, 18, 0, 0) },
            Padding = new Thickness(0, 4, 0, 14),
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
        root.Add(_videoView, 0, 0);
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
        _player.Playing += (_, _) => Dispatcher.Dispatch(() => { _playPause.Text = IcPause; RestartHideTimer(); });
        _player.Paused += (_, _) => Dispatcher.Dispatch(() => { _playPause.Text = IcPlay; StopHideTimer(); });

        // Stop + release when the user backs out of (or closes) the player.
        NavigatedFrom += (_, _) => { try { _player.Stop(); } catch { /* already stopped */ } };
    }

    // ── Lifecycle: force landscape + immersive while open; pause on background ──
    protected override void OnAppearing()
    {
        base.OnAppearing();
        SetImmersive(true);
        // Hiding the bars doesn't survive the modal-present + forced rotation on some OEMs, so re-assert once
        // the transition settles (MainActivity also re-applies on config change / resume).
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(350), () => SetImmersive(true));
        _window = Application.Current?.Windows.FirstOrDefault();
        if (_window is not null)
        {
            _window.Stopped += OnWindowStopped;
            _window.Resumed += OnWindowResumed;
        }
        RestartHideTimer();
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
        StopHideTimer();
        SetImmersive(false);
    }

    // App backgrounded (Home / recents) → pause so audio doesn't keep playing behind an inactive app.
    private void OnWindowStopped(object? sender, EventArgs e)
    {
        try { if (_player.IsPlaying) _player.SetPause(true); } catch { /* not ready */ }
    }

    // Returning from background: the native video surface was destroyed while away, so the MediaPlayer is
    // still bound to a dead surface — playback resumes with audio but a black frame. Re-bind the VideoView to
    // the player so libVLC re-attaches to the freshly recreated surface and the picture comes back. Delayed so
    // the surface exists by the time we re-bind.
    private void OnWindowResumed(object? sender, EventArgs e)
    {
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), () =>
        {
            try { _videoView.MediaPlayer = null; _videoView.MediaPlayer = _player; } catch { /* not ready */ }
        });
    }

    private static void SetImmersive(bool on)
    {
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity is null) return;
        if (on)
        {
            activity.RequestedOrientation = Android.Content.PM.ScreenOrientation.SensorLandscape;
            AndroidImmersive.Enter(activity);
        }
        else
        {
            AndroidImmersive.Exit(activity);
            activity.RequestedOrientation = Android.Content.PM.ScreenOrientation.Unspecified;
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
    private void OpenSheet(string title, View content)
    {
        _sheetTitle.Text = title;
        _sheetContent.Content = content;
        _sheetOverlay.IsVisible = true;
        _controls.IsVisible = true;
        StopHideTimer();
    }

    private void CloseSheet()
    {
        _sheetOverlay.IsVisible = false;
        ShowControls();
    }

    // A single tappable list row with a selection check. onTap decides whether to close the sheet.
    private View Row(string text, bool selected, Action onTap)
    {
        var label = new Label
        {
            Text = text,
            TextColor = selected ? Accent : Colors.White,
            FontSize = 15,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var check = new Label
        {
            Text = selected ? IcCheck : "",
            FontFamily = IconFont,
            TextColor = Accent,
            FontSize = 16,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End,
        };
        var row = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
            Padding = new Thickness(16, 11),
        };
        row.Add(label, 0, 0);
        row.Add(check, 1, 0);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        row.GestureRecognizers.Add(tap);
        return row;
    }

    private static ScrollView ScrollList(IEnumerable<View> rows)
    {
        var stack = new VerticalStackLayout { Spacing = 0 };
        foreach (var r in rows) stack.Add(r);
        return new ScrollView { Content = stack, MaximumHeightRequest = 240 };
    }

    private void OpenSpeedSheet()
    {
        float current;
        try { current = _player.Rate; } catch { current = 1f; }

        var rows = new[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f }.Select(rate =>
            Row(
                rate == 1.0f ? "Normal (1.0×)" : $"{rate:0.##}×",
                Math.Abs(current - rate) < 0.001f,
                () => { try { _player.SetRate(rate); } catch { } CloseSheet(); }));

        OpenSheet("Playback Speed", ScrollList(rows));
    }

    private void OpenAudioSheet()
    {
        var rows = new List<View>();
        try
        {
            var current = _player.AudioTrack;
            foreach (var t in _player.AudioTrackDescription)
            {
                var id = t.Id;
                rows.Add(Row(
                    string.IsNullOrWhiteSpace(t.Name) ? $"Track {id}" : t.Name,
                    id == current,
                    () => { try { _player.SetAudioTrack(id); } catch { } CloseSheet(); }));
            }
        }
        catch { /* tracks not parsed yet */ }

        if (rows.Count == 0)
            rows.Add(Row("No audio tracks", false, CloseSheet));

        OpenSheet("Audio Tracks", ScrollList(rows));
    }

    private void OpenScalingSheet()
    {
        var rows = new[]
        {
            Row("Fit", _scaleMode == ScaleMode.Fit, () => ApplyScale(ScaleMode.Fit)),
            Row("16:9", _scaleMode == ScaleMode.SixteenNine, () => ApplyScale(ScaleMode.SixteenNine)),
            Row("4:3", _scaleMode == ScaleMode.FourThree, () => ApplyScale(ScaleMode.FourThree)),
            Row("Fill", _scaleMode == ScaleMode.Fill, () => ApplyScale(ScaleMode.Fill)),
        };
        OpenSheet("Video scaling", ScrollList(rows));
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
        CloseSheet();
    }

    // ── Subtitles: Stremio-style language · tracks · options columns ────────────
    private void OpenSubtitleSheet()
    {
        var tracks = new List<(int Id, string Name)>();
        try
        {
            foreach (var t in _player.SpuDescription)
                if (t.Id != -1)
                    tracks.Add((t.Id, string.IsNullOrWhiteSpace(t.Name) ? $"Track {t.Id}" : t.Name));
        }
        catch { /* tracks not parsed yet */ }

        int currentSpu;
        try { currentSpu = _player.Spu; } catch { currentSpu = -1; }

        var langs = tracks.Select(t => LangOf(t.Name)).Distinct().ToList();

        // Default the selected language column to the current track's language, else the first available.
        if (_subLang is null || !langs.Contains(_subLang))
        {
            var cur = tracks.FirstOrDefault(t => t.Id == currentSpu);
            _subLang = cur.Name is not null ? LangOf(cur.Name) : langs.FirstOrDefault();
        }

        // Left column: "Off" + one entry per language. Selecting a language just re-filters (keeps the sheet
        // open); selecting "Off" disables subtitles and closes.
        var langRows = new List<View>
        {
            Row("Off", currentSpu == -1, () => { try { _player.SetSpu(-1); } catch { } CloseSheet(); }),
        };
        foreach (var lang in langs)
        {
            var l = lang;
            langRows.Add(Row(l, l == _subLang, () => { _subLang = l; OpenSubtitleSheet(); }));
        }

        // Middle column: tracks in the selected language. Selecting one applies + closes.
        var trackRows = tracks
            .Where(t => LangOf(t.Name) == _subLang)
            .Select(t =>
            {
                var id = t.Id;
                return Row(t.Name, id == currentSpu, () => { try { _player.SetSpu(id); } catch { } CloseSheet(); });
            })
            .ToList();
        if (trackRows.Count == 0)
            trackRows.Add(Row(tracks.Count == 0 ? "No subtitles available" : "—", false, () => { }));

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new() { Width = new GridLength(1.1, GridUnitType.Star) }, // languages
                new() { Width = new GridLength(1.6, GridUnitType.Star) }, // tracks
                new() { Width = new GridLength(1.3, GridUnitType.Star) }, // options
            },
            ColumnSpacing = 8,
            Padding = new Thickness(8, 0, 8, 0),
        };
        grid.Add(Column("Language", ScrollList(langRows)), 0, 0);
        grid.Add(Column("Track", ScrollList(trackRows)), 1, 0);
        grid.Add(Column("Options", BuildSubtitleOptions()), 2, 0);

        OpenSheet("Subtitles", grid);
    }

    private View Column(string heading, View body)
    {
        var head = new Label
        {
            Text = heading,
            TextColor = Color.FromRgba(255, 255, 255, 150),
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            Padding = new Thickness(16, 4, 8, 4),
        };
        return new VerticalStackLayout { Spacing = 0, Children = { head, body } };
    }

    // Right column: subtitle delay (the only subtitle property LibVLCSharp exposes at runtime; size/offset
    // have no clean runtime API). −/+ adjust in 0.1s steps and DON'T close the sheet.
    private View BuildSubtitleOptions()
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
            WidthRequest = 72,
            HorizontalTextAlignment = TextAlignment.Center,
        };

        void Adjust(long stepUs)
        {
            try { var next = _player.SpuDelay + stepUs; _player.SetSpuDelay(next); value.Text = FormatDelay(next); }
            catch { }
        }

        var minus = GlyphButton(IcRemove, 22, 40);
        minus.Clicked += (_, _) => Adjust(-100_000); // −0.1s
        var plus = GlyphButton(IcAdd, 22, 40);
        plus.Clicked += (_, _) => Adjust(+100_000);   // +0.1s

        var caption = new Label
        {
            Text = "Delay",
            TextColor = Colors.White,
            FontSize = 14,
            HorizontalOptions = LayoutOptions.Center,
        };
        var stepper = new HorizontalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.Center,
            Children = { minus, value, plus },
        };
        return new VerticalStackLayout
        {
            Spacing = 6,
            Padding = new Thickness(8, 8, 8, 0),
            Children = { caption, stepper },
        };
    }

    // Extract the language label from a libVLC SPU track name, e.g. "English (SDH) - [English]" → "English".
    private static string LangOf(string name)
    {
        var open = name.LastIndexOf('[');
        var close = name.LastIndexOf(']');
        if (open >= 0 && close > open) return name.Substring(open + 1, close - open - 1).Trim();
        var cut = name.IndexOfAny(new[] { '(', '-' });
        return (cut > 0 ? name[..cut] : name).Trim();
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
    private static Button GlyphButton(string glyph, double fontSize, double size) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = fontSize,
        TextColor = Colors.White,
        BackgroundColor = Colors.Transparent,
        BorderWidth = 0,
        Padding = new Thickness(0),
        WidthRequest = size,
        HeightRequest = size,
    };

    private static Label TimeLabel(string text) => new()
    {
        Text = text,
        TextColor = Colors.White,
        FontSize = 12,
        VerticalOptions = LayoutOptions.Center,
    };

    // Inline icon + label (icon on the left) — keeps the action row to a single short line.
    private View ActionButton(string icon, string label, Action onTap)
    {
        var stack = new HorizontalStackLayout
        {
            Spacing = 6,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Padding = new Thickness(6, 4),
            Children =
            {
                new Label { Text = icon, FontFamily = IconFont, FontSize = 18, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center },
                new Label { Text = label, FontSize = 13, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center },
            },
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        stack.GestureRecognizers.Add(tap);
        return stack;
    }
}
