using LibVLCSharp.Shared;
using LibVLCSharp.MAUI;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls.Shapes;
// Alias (not a namespace import) — LibVLCSharp.Shared also defines a SubtitleTrack, so a plain
// `using AniSync.Client.Services;` makes the name ambiguous (CS0104).
using SubtitleTrack = AniSync.Client.Services.SubtitleTrack;
// Alias (not a full namespace import) for the same reason as SubtitleTrack above.
using PlaybackLanguages = AniSync.Client.Services.PlaybackLanguages;
using SkipMark = AniSync.Client.Services.SkipMark;
// NOTE: the libVLC player type is referenced as the fully-qualified LibVLCSharp.Shared.MediaPlayer
// below. On iOS the SDK exposes a top-level `MediaPlayer` namespace (Apple's MediaPlayer.framework
// binding), so the bare name resolves to that namespace (CS0118) and a `using MediaPlayer = …` alias
// collides with it (CS0576) — qualifying is the only clean option. (view.MediaPlayer member access
// and MediaPlayer*EventArgs are unaffected.)

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

    // Optional build identifier shown faintly in the top bar to confirm which APK is installed. Empty = hidden.
    private const string BuildTag = "";

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

    private readonly LibVLCSharp.Shared.MediaPlayer _player;
    private VideoView _videoView;
    private readonly Grid _root;
    private readonly Slider _seek;
    private readonly Button _playPause;
    private readonly View _transport;          // centre rewind/play/forward — hidden while the spinner is up
    private readonly Label _position;
    private readonly Label _duration;
    private readonly Label _resLabel;   // resolution / "4K" indicator in the top bar
    private readonly Grid _controls;
    private readonly Grid _sheetOverlay;
    private readonly Label _sheetTitle;
    private readonly ContentView _sheetContent;
    private readonly IDispatcherTimer _hideTimer;

    // Buffering / connecting overlay (spinner + title) shown until the first frame decodes and whenever the
    // stream re-buffers, so a debrid link that's slow to start no longer looks like a frozen black screen.
    private readonly Grid _loading;
    private readonly ActivityIndicator _spinner;

    // Android TV / Google TV is driven by a D-pad remote — there's no tap to bring the chrome back, so we
    // never auto-hide it there, and we move focus onto the play button so the remote has a landing spot.
    private readonly bool _isTv = DeviceInfo.Current.Idiom == DeviceIdiom.TV;

    // Set by the foreground TV player so MainActivity.DispatchKeyEvent can route remote D-pad/OK keys
    // here: when the chrome has auto-hidden, the first press re-summons it (and is swallowed). Returns
    // true when it consumed the press. Null whenever no TV player is foreground.
    internal static Func<bool>? TvWakeOnKey;

    // External subtitle tracks (proxied OpenSubtitles URLs + real language labels) from the API. Shown in the
    // sheet up-front and attached to libVLC on demand when picked — they aren't pre-loaded as slaves.
    private readonly IReadOnlyList<SubtitleTrack> _externalSubs;
    private HashSet<int>? _embeddedSpuIds;   // SPU ids from the media file, captured before any slave is added
    private readonly ConcurrentDictionary<string, int> _slaveSpu = new();  // external sub url → its SPU id once libVLC has parsed the added slave
    private string? _selectedExternalUrl;    // url of the external sub the user attached (drives the check)
    private bool _defaultSubScheduled;       // one-shot guard for the default-track pass
    private bool _userPickedSub;             // the user chose a subtitle → don't override with the default
    private bool _userPickedAudio;           // the user chose an audio track → don't override with the default
    private readonly string _preferredAudioLang; // ISO 639-1 (account setting; "en" default)
    private readonly string _preferredSubLang;
    private readonly string? _fetchDiag;         // TEMP: page-side subtitle-fetch diagnostics for the sheet title

    // Intro/recap/outro skip bands (absolute seconds) + the corner "Skip …" button. Shown while the
    // current position is inside a band; tapping seeks just past it. Phone-only (TV uses ExoPlayerPage).
    // The _skip* fields are the API-provided bands (AniSkip / introdb); the _eff* fields are what the
    // button actually uses — seeded from the API bands, then filled from embedded media chapters for any
    // band type the API left null (chapter fallback, phone only).
    private readonly SkipMark? _skipIntro;
    private readonly SkipMark? _skipOutro;
    private readonly SkipMark? _skipRecap;
    private SkipMark? _effIntro, _effOutro, _effRecap;
    private bool _chaptersTried;
    private readonly Button _skipButton;
    private double _skipTarget;

    private bool _seeking;
    // Rewind/forward (±30s) accumulation: rapid taps fold into one seek instead of firing a seek (and its
    // re-buffer) per tap. Each tap moves a pending target and the slider/clock immediately; the actual
    // _player.Time is committed once the taps stop (debounced). _suppressSpinnerUntil keeps the buffering
    // overlay from popping for a seek's own re-buffer so the transport stays visible — i.e. you can keep
    // tapping to skip a 90s intro without waiting on the loader between taps.
    private bool _seekPending;
    private long _pendingSeekTarget;
    private int _seekGen;
    private DateTime _suppressSpinnerUntil;
    private bool _started;   // one-shot guard: playback is started once, when the video surface is ready
    private ScaleMode _scaleMode = ScaleMode.Fit;
    private string? _subLang;                        // selected language column in the subtitle sheet
    private Microsoft.Maui.Controls.Window? _window; // for the background-pause hook

    private enum ScaleMode { Fit, Crop, Fill, SixteenNine, FourThree }

    public VlcPlayerPage(LibVLCSharp.Shared.MediaPlayer player, string title, IReadOnlyList<SubtitleTrack>? externalSubs = null,
        string? preferredAudioLang = null, string? preferredSubLang = null, string? fetchDiag = null,
        SkipMark? skipIntro = null, SkipMark? skipOutro = null, SkipMark? skipRecap = null)
    {
        _player = player;
        _externalSubs = externalSubs ?? Array.Empty<SubtitleTrack>();
        _preferredAudioLang = PlaybackLanguages.Normalize(preferredAudioLang);
        _preferredSubLang = PlaybackLanguages.Normalize(preferredSubLang);
        _fetchDiag = fetchDiag;
        _skipIntro = skipIntro;
        _skipOutro = skipOutro;
        _skipRecap = skipRecap;
        // Effective bands start as the API bands; chapter fallback fills any null type once playing.
        _effIntro = skipIntro;
        _effOutro = skipOutro;
        _effRecap = skipRecap;
        Title = title;
        BackgroundColor = Colors.Black;
        NavigationPage.SetHasNavigationBar(this, false);
        // Let the video bleed under the notch / system-bar regions (MAUI 10 safe-area control). Without this,
        // MAUI pads the page away from the display cutout, so Fit sits off-centre and Crop/Fill stop at the
        // notch edge. The on-screen controls carry their own padding so they stay clear of the cutout.
        SafeAreaEdges = SafeAreaEdges.None;

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

        var buildLabel = new Label
        {
            Text = BuildTag,
            TextColor = Color.FromRgba(255, 255, 255, 90),
            FontSize = 11,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End,
        };

        // Decoded-stream resolution ("4K" / "1080p" …), filled once playback starts. Confirms at a
        // glance whether a 4K source is actually being decoded at 4K (the "true Ultra HD?" question).
        _resLabel = new Label
        {
            Text = "",
            TextColor = Colors.White,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End,
        };

        var topBar = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 10,
            Padding = new Thickness(4, 0, 12, 0),
            BackgroundColor = Scrim,
            VerticalOptions = LayoutOptions.Start,
        };
        topBar.Add(back, 0, 0);
        topBar.Add(titleLabel, 1, 0);
        topBar.Add(_resLabel, 2, 0);
        topBar.Add(buildLabel, 3, 0);

        // ── Centre transport: rewind 30s · play/pause · forward 30s (uniform size) ─
        var rewind = GlyphButton(IcReplay30, 32, 60);
        rewind.Clicked += (_, _) => Nudge(-30_000);

        _playPause = GlyphButton(IcPause, 32, 60);
        _playPause.Clicked += (_, _) => TogglePlay();

        var forward = GlyphButton(IcForward30, 32, 60);
        forward.Clicked += (_, _) => Nudge(+30_000);

        _transport = new HorizontalStackLayout
        {
            Spacing = 34,
            IsVisible = false,   // playback opens on the spinner; revealed once we're buffering-complete / playing
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            // Centred mid-screen, nowhere near the cutout — consuming the notch inset here would only
            // shift the buttons off-centre.
            SafeAreaEdges = SafeAreaEdges.None,
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

        // None on the wrapper so the safe-area insets flow DOWN to the individual bars: each bar then pads
        // its own content away from the notch while its scrim background still bleeds to the screen edge
        // (safe-area is applied as padding, and backgrounds cover the padding area). Stremio looks the same.
        _controls = new Grid
        {
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
            SafeAreaEdges = SafeAreaEdges.None,
        };
        _controls.Add(topBar, 0, 0);
        _controls.Add(_transport, 0, 1);
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

        // None so the dimmed backdrop covers the whole screen; the sheet panel itself keeps the default
        // safe-area behaviour, so its rows stay clear of the notch while its background spans full width.
        _sheetOverlay = new Grid { IsVisible = false, SafeAreaEdges = SafeAreaEdges.None };
        _sheetOverlay.Add(backdrop);
        _sheetOverlay.Add(sheetPanel);

        // ── Buffering / connecting overlay (centre spinner) ─────────────────────
        _spinner = new ActivityIndicator
        {
            IsRunning = true,
            Color = Accent,
            WidthRequest = 48,
            HeightRequest = 48,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        // InputTransparent so a tap still reaches the root (toggling the chrome) while it's up. Visible from
        // the start — playback begins black, so the spinner is the only "we're working on it" signal.
        _loading = new Grid { InputTransparent = true, SafeAreaEdges = SafeAreaEdges.None };
        _loading.Add(_spinner);

        // ── Compose: video, then controls, then the sheet on top ───────────────
        // SafeAreaEdges=None on the PAGE alone isn't enough: MAUI 10 also applies safe-area insets at the
        // layout level, and fs9's diagnostics showed the root Grid padding its children 152px away from the
        // notch (the page view spanned the full 2772px display while the content started at x=152).
        // AniSkip corner button — shown only inside an OP/ED band, independent of the auto-hiding chrome
        // (Stremio-style: it stays put so you can skip even after the controls fade). Hidden until a band.
        _skipButton = new Button
        {
            IsVisible = false,
            TextColor = Colors.White,
            BackgroundColor = Color.FromRgba(0, 0, 0, 180),
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            Padding = new Thickness(20, 12),
            CornerRadius = 8,
            BorderColor = Color.FromRgba(255, 255, 255, 90),
            BorderWidth = 1,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 28, 92),   // clear of the bottom control bar
        };
        _skipButton.Clicked += OnSkipClicked;

        _root = new Grid { SafeAreaEdges = SafeAreaEdges.None };
        _root.Add(_videoView, 0, 0);
        _root.Add(_controls, 0, 0);
        _root.Add(_loading, 0, 0);     // over the video, under the controls' tap target + the sheet
        _root.Add(_skipButton, 0, 0);  // above the chrome so it survives auto-hide, below the sheet
        _root.Add(_sheetOverlay, 0, 0);

        // Tap-to-toggle the chrome — touch heads only. A TapGestureRecognizer on the root makes it
        // focusable+clickable on Android, so on a TV it STEALS D-pad focus from the control buttons
        // and OK (DPAD_CENTER) just fires this toggle instead of activating the focused control
        // (the "arrows do nothing, OK shows/hides the overlay, nothing clickable" symptom). On TV the
        // chrome auto-hides and any D-pad key re-summons it (MainActivity → OnTvWakeKey), so no tap.
        if (!_isTv)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ToggleControls();
            _root.GestureRecognizers.Add(tap);
        }
        Content = _root;

        // Auto-hide the chrome after a few seconds of inactivity.
        _hideTimer = Dispatcher.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(3.5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideControls();

        // Keep the UI in sync (libVLC raises these on its own thread → marshal to the UI thread).
        _player.PositionChanged += OnPositionChanged;
        _player.LengthChanged += OnLengthChanged;
        // KeepAwake: hold the screen on while playing or loading (so the phone never locks mid-movie),
        // and release it when paused / ended / closed so it can sleep normally.
        _player.Playing += (_, _) => Dispatcher.Dispatch(() =>
        {
            _playPause.Text = IcPause; SetLoading(false); KeepAwake(true); RestartHideTimer();
            ShowResolution();
            if (_isTv) _playPause.Focus();
            // Default the subtitle to English (matching the web) once tracks have had a moment to parse.
            if (!_defaultSubScheduled) { _defaultSubScheduled = true; Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(700), () => { ApplyDefaultSubtitle(); ApplyDefaultAudio(); }); }
            // Chapter fallback for skip bands the API didn't cover (chapters lag the first frame).
            if (!_chaptersTried) Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(500), TryApplyChapterFallback);
        });
        _player.Paused += (_, _) => Dispatcher.Dispatch(() => { _playPause.Text = IcPlay; StopHideTimer(); KeepAwake(false); });
        // Buffering climbs 0→100 on connect and re-fires on a re-buffer; show the spinner until it's full.
        // Buffering means we're loading toward playback, so keep the screen awake through it too.
        _player.Buffering += (_, e) => Dispatcher.Dispatch(() =>
        {
            var loading = e.Cache < 100f;
            if (loading) KeepAwake(true);
            // A seek's own re-buffer must not pop the overlay (it hides the transport, blocking further
            // ±30s taps). Skip showing the spinner inside the post-seek window; still clear it when full.
            if (loading && DateTime.UtcNow < _suppressSpinnerUntil) return;
            SetLoading(loading);
        });
        _player.EndReached += (_, _) => Dispatcher.Dispatch(() => KeepAwake(false));
        _player.EncounteredError += (_, _) => Dispatcher.Dispatch(() => KeepAwake(false));

        // Stop + release when the user backs out of (or closes) the player.
        NavigatedFrom += (_, _) => { try { _player.Stop(); } catch { /* already stopped */ } KeepAwake(false); };

        // Re-assert immersive once the page's native view is attached to its (modal) window, and start
        // playback only when the video surface is actually ready (see BeginPlaybackWhenSurfaceReady).
        Loaded += (_, _) => { ApplyImmersiveToView(); BeginPlaybackWhenSurfaceReady(); };
    }

    // Start playback once the VideoView's native surface exists, rather than the instant the page is
    // presented — so libVLC always has a valid output surface when it starts. Hooks the native
    // SurfaceHolder, with a timed safety net so playback never hangs unstarted if the surface can't be
    // found (e.g. a non-SurfaceView backend).
    private void BeginPlaybackWhenSurfaceReady()
    {
        if (_started) return;
        var hooked = false;
#if ANDROID
        if (_videoView.Handler?.PlatformView is global::Android.Views.View v)
            hooked = AndroidVideoSurface.WhenReady(v, () => Dispatcher.Dispatch(StartPlaybackOnce));
#endif
        // Safety net: if we couldn't hook the surface (or the callback never arrives), start anyway. Longer
        // when hooked (just insurance behind the real signal); shorter when not (best-effort initial delay).
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(hooked ? 2500 : 600), StartPlaybackOnce);
    }

    private void StartPlaybackOnce()
    {
        if (_started) return;
        _started = true;
        try { _player.Play(); } catch { /* torn down before the surface was ready */ }
    }

    // ── Lifecycle: force landscape + immersive while open; pause on background ──
    protected override void OnAppearing()
    {
        base.OnAppearing();
        // TV only: let the Android activity route remote keys here so a hidden chrome can be re-summoned.
        if (_isTv) TvWakeOnKey = OnTvWakeKey;
        SetImmersive(true);
        ApplyImmersiveToView();
        // The player opens on the loading spinner — hold the screen on from the start.
        KeepAwake(true);
        // Hiding the bars doesn't survive the modal-present + forced rotation on some OEMs, so re-assert once
        // the transition settles (MainActivity also re-applies on config change / resume).
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(350), () => { SetImmersive(true); ApplyImmersiveToView(); });
        _window = Application.Current?.Windows.FirstOrDefault();
        if (_window is not null)
        {
            _window.Stopped += OnWindowStopped;
            _window.Resumed += OnWindowResumed;
        }
        RestartHideTimer();
    }

    // Target the player view's OWN hosting window for immersive. MAUI presents the modal in a separate window,
    // so flags on the Activity window (SetImmersive) never reach it — this resolves the right window from the
    // view itself. This is the lever that actually hides the bars over the modal.
    private void ApplyImmersiveToView()
    {
#if ANDROID
        if (Handler?.PlatformView is global::Android.Views.View v)
            AndroidImmersive.ApplyToView(v);
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Stop routing remote keys here once this player is gone (only one is ever foreground).
        if (_isTv) TvWakeOnKey = null;
        if (_window is not null)
        {
            _window.Stopped -= OnWindowStopped;
            _window.Resumed -= OnWindowResumed;
            _window = null;
        }
        StopHideTimer();
        SetImmersive(false);
        // Always release the wake-lock when leaving the player so it can't leak past the session.
        KeepAwake(false);
        // Tear the native video surface down deterministically (LibVLCSharp issue #659). The libVLC
        // platform VideoView removes its AWindow surface-callback in Detach(); left to the GC, that
        // finalizer runs AFTER the MediaPlayer's AWindow has been disposed and throws
        // ObjectDisposedException on the finalizer thread — the AWindow crash in the diagnostic log.
        // Detaching the player + disconnecting the handler here removes the callback while the AWindow
        // is still valid and disposes the platform view so its finalizer is suppressed. Mirrors
        // ExoPlayerPage's teardown.
        TeardownVideoView(_videoView);
    }

    // Detach the player from a VideoView and dispose its platform view now, while its native AWindow is
    // still alive — see OnDisappearing / LibVLCSharp #659. Static + null-tolerant so it's safe from both
    // the page-close path and the background-resume surface swap.
    private static void TeardownVideoView(VideoView view)
    {
        try { view.MediaPlayer = null; } catch { /* surface / player already gone */ }
        try { view.Handler?.DisconnectHandler(); } catch { /* already disconnected */ }
    }

    // App backgrounded (Home / recents) → pause so audio doesn't keep playing behind an inactive app.
    // Remember that WE paused it (vs. the user pausing) so resume only auto-plays what was actually playing.
    private bool _pausedForBackground;
    private void OnWindowStopped(object? sender, EventArgs e)
    {
        try { if (_player.IsPlaying) { _player.SetPause(true); _pausedForBackground = true; } } catch { /* not ready */ }
    }

    // Returning from background: Android destroyed the native video surface while away, so the MediaPlayer is
    // bound to a dead surface — playback resumes with audio but a black frame. We swap in a FRESH VideoView so
    // its handler builds a new live surface and attaches the player to it. Two things the old version missed,
    // which left it black with audio-only:
    //   1. Detach the player from the OLD view FIRST — otherwise removing it fires surfaceDestroyed, which
    //      tears the video output back off the NEW surface we just attached (a destroy/create race).
    //   2. Resume playback + nudge the position once the new surface is up, so libVLC actually decodes a
    //      frame onto it (a paused player paints no video output → black).
    private void OnWindowResumed(object? sender, EventArgs e)
    {
        SetLoading(true); // spinner instead of a black frame while the surface is rebuilt
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
        {
            try
            {
                _videoView.MediaPlayer = null;   // detach from the dead surface BEFORE removing it
                _root.Remove(_videoView);
                // Dispose the swapped-out view's platform peer too, so it doesn't reach the GC finalizer
                // and crash on the dead AWindow (LibVLCSharp #659) — same reason as the page-close path.
                try { _videoView.Handler?.DisconnectHandler(); } catch { /* already disconnected */ }

                var fresh = new VideoView
                {
                    VerticalOptions = LayoutOptions.Fill,
                    HorizontalOptions = LayoutOptions.Fill,
                };
                _root.Children.Insert(0, fresh); // behind the controls + sheet overlay
                fresh.MediaPlayer = _player;     // attaches to the new surface on surfaceCreated
                _videoView = fresh;
            }
            catch { /* not ready */ }

            // Let the new surface get created, then resume + nudge so a frame paints onto it.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), () =>
            {
                try
                {
                    if (_pausedForBackground) { _player.SetPause(false); _pausedForBackground = false; }
                    var at = _player.Time;
                    // Seek back a touch to force libVLC to flush + decode a fresh frame onto the new surface
                    // (re-binding alone can leave the last decode buffered against the old, dead surface).
                    if (at > 0) _player.Time = at > 1500 ? at - 1500 : 0;
                }
                catch { /* not ready */ }
            });
        });
    }

    // Hold/release the screen-on lock (Android FLAG_KEEP_SCREEN_ON via MAUI's cross-platform API), so the
    // phone never auto-locks while a movie is playing or loading. Idempotent; safe to call repeatedly.
    private static void KeepAwake(bool on)
    {
        try { DeviceDisplay.Current.KeepScreenOn = on; } catch { /* unsupported / not ready */ }
    }

    // Default the subtitle track. libVLC auto-selects the file's default SPU (which "opened on French");
    // instead honour the user's preferred subtitle language, falling back to English, then OFF — never a
    // surprise foreign track. External OS subs and embedded SPU tracks are both considered, in that order.
    private void ApplyDefaultSubtitle()
    {
        if (_userPickedSub || _selectedExternalUrl is not null) return; // user already chose
        try
        {
            // ISO language tags for the in-file SPU tracks — libVLC names SPU tracks by codec
            // ("ASS"/"SRT"), not language, so the real language sits on the media tracks (same
            // source the picker sheet uses). Lets us match an embedded track by its tag even
            // when its name carries no language word.
            var spuLang = new Dictionary<int, string?>();
            try
            {
                if (_player.Media?.Tracks is { } tracks)
                    foreach (var track in tracks)
                        if (track.TrackType == TrackType.Text)
                            spuLang[track.Id] = track.Language;
            }
            catch { /* media not parsed yet — fall back to name matching */ }

            foreach (var lang in PreferredThenEnglish(_preferredSubLang))
            {
                // Prefer an embedded (in-file) track in this language over a downloaded OS sub:
                // embedded subs are better timed/styled and carry positioned sign translations,
                // so when both exist for the chosen language the in-file one wins.
                foreach (var t in _player.SpuDescription)
                {
                    if (t.Id == -1) continue;
                    spuLang.TryGetValue(t.Id, out var code);
                    if (LangMatches(lang, code, t.Name)) { _player.SetSpu(t.Id); return; }
                }

                var ext = _externalSubs.FirstOrDefault(s => LangMatches(lang, s.Language, s.Label));
                if (ext is not null) { AttachExternalSub(ext.Url); return; }
            }
            _player.SetSpu(-1); // nothing in the preferred language or English → off, like the web
        }
        catch { /* tracks not parsed yet */ }
    }

    // Preselect the audio track in the preferred language (e.g. a dual-audio release defaulting to
    // Japanese), falling back to English. If neither exists, leave libVLC's default (first track) — unlike
    // subtitles we never turn audio off.
    private void ApplyDefaultAudio()
    {
        if (_userPickedAudio) return;
        try
        {
            var current = _player.AudioTrack;
            foreach (var lang in PreferredThenEnglish(_preferredAudioLang))
            {
                foreach (var t in _player.AudioTrackDescription)
                {
                    if (t.Id == -1) continue; // the "Disable" pseudo-track
                    if (LangMatches(lang, null, t.Name))
                    {
                        if (t.Id != current) _player.SetAudioTrack(t.Id);
                        return;
                    }
                }
            }
        }
        catch { /* tracks not parsed yet */ }
    }

    // The preferred language, then English as the fallback (deduped) — the lookup order both defaults use.
    private static IEnumerable<string> PreferredThenEnglish(string? preferred)
    {
        var p = PlaybackLanguages.Normalize(preferred);
        yield return p;
        if (p != PlaybackLanguages.Default) yield return PlaybackLanguages.Default;
    }

    // Attach an external subtitle off the UI thread. AddSlave fetches the file, and doing that on the UI
    // thread froze the app (ANR) for a slow/large sub — the reported "swap to English freezes".
    //
    // We do NOT trust AddSlave's select:true to actually switch the displayed track: when the media file
    // has an embedded default SPU (the "opened on French" case), libVLC keeps showing it — the slave is
    // added but not selected — so the user picks English yet still sees French. Instead we find the
    // freshly-parsed slave's SPU id and select it ourselves (SetSpu), which is reliable.
    private void AttachExternalSub(string url)
    {
        _selectedExternalUrl = url;
        _embeddedSpuIds ??= CaptureSpuIds();   // freeze the file's own SPU ids before adding any slave

        // Already loaded once → just (re)select it. A second AddSlave of the same URL won't re-fire
        // selection, so re-picking has to go straight to SetSpu.
        if (_slaveSpu.TryGetValue(url, out var knownId))
        {
            try { _player.SetSpu(knownId); } catch { /* track gone */ }
            return;
        }

        _ = Task.Run(async () =>
        {
            try { _player.AddSlave(MediaSlaveType.Subtitle, url, select: true); }
            catch { return; }   // unreachable / malformed sub

            // The slave loads asynchronously; once its SPU track appears (the id that's neither embedded
            // nor an already-mapped slave) select it — but only if the user still wants this one.
            for (var i = 0; i < 40; i++)   // up to ~6s, polling every 150ms
            {
                if (FindNewSpuId() is int id)
                {
                    _slaveSpu[url] = id;
                    if (_selectedExternalUrl == url)
                        Dispatcher.Dispatch(() => { try { _player.SetSpu(id); } catch { /* track gone */ } });
                    return;
                }
                await Task.Delay(150);
            }
        });
    }

    // Snapshot the player's current SPU track ids (skips the -1 "disable" pseudo-track).
    private HashSet<int> CaptureSpuIds()
    {
        var ids = new HashSet<int>();
        try { foreach (var t in _player.SpuDescription) if (t.Id != -1) ids.Add(t.Id); } catch { /* not parsed */ }
        return ids;
    }

    // The SPU id of a just-added external slave: present in SpuDescription but neither one of the file's
    // embedded ids nor an already-mapped slave. Null until libVLC has parsed the new slave.
    private int? FindNewSpuId()
    {
        try
        {
            foreach (var t in _player.SpuDescription)
            {
                if (t.Id == -1) continue;
                if (_embeddedSpuIds is { } emb && emb.Contains(t.Id)) continue;
                if (_slaveSpu.Values.Contains(t.Id)) continue;
                return t.Id;
            }
        }
        catch { /* tracks not parsed yet */ }
        return null;
    }

    // Does a track (its language code and/or display name) belong to the target language? Matches the
    // 2-letter code, the 3-letter/long variants via the LangNames map, and the language name in the label.
    private static bool LangMatches(string targetCode, string? trackCode, string? trackName)
    {
        var target = PlaybackLanguages.Normalize(targetCode);
        var targetName = (LangNames.TryGetValue(target, out var tn) ? tn : PlaybackLanguages.DisplayName(target)).ToLowerInvariant();

        var code = (trackCode ?? "").Trim().ToLowerInvariant();
        if (code.Length > 0)
        {
            if (code == target) return true;
            if (code.Length > 2 && code[..2] == target) return true;                       // "en-US" → "en"
            if (LangNames.TryGetValue(code, out var cn) && cn.ToLowerInvariant() == targetName) return true; // "eng"/"jpn"
        }
        var name = (trackName ?? "").ToLowerInvariant();
        return targetName.Length > 0 && name.Contains(targetName);
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
            // Accumulate from the pending target (if a seek is still queued) so repeated taps add up
            // instead of each measuring from the same live position.
            var target = (_seekPending ? _pendingSeekTarget : _player.Time) + deltaMs;
            if (target < 0) target = 0;
            if (len > 0 && target > len) target = len;
            _pendingSeekTarget = target;
            _seekPending = true;
            _seeking = true;   // freeze position-driven slider updates while taps accumulate
            // Immediate feedback: jump the slider + clock to the pending target even though the decoder
            // hasn't moved yet, so a burst of taps reads as one smooth scrub.
            if (len > 0) _seek.Value = Math.Clamp((double)target / len, 0, 1);
            UpdateTimes();
            // Debounce the real seek: commit only once taps stop, collapsing a burst into a single seek.
            var gen = ++_seekGen;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), () => CommitSeek(gen));
        }
        catch { /* not seekable yet */ }
        RestartHideTimer();
    }

    // Apply the accumulated ±30s target, unless a newer tap has arrived (gen bumped) and is still
    // accumulating. Suppress the buffering overlay for the seek's re-buffer so the transport stays
    // visible and tappable — the whole point is to let the user keep skipping without the loader.
    private void CommitSeek(int gen)
    {
        if (gen != _seekGen || !_seekPending) return;
        try { _player.Time = _pendingSeekTarget; } catch { /* not seekable yet */ }
        _seekPending = false;
        _seeking = false;
        _suppressSpinnerUntil = DateTime.UtcNow.AddSeconds(4);
    }

    private async Task CloseAsync()
    {
        try { await Navigation.PopModalAsync(); } catch { /* already closing */ }
    }

    // Hardware / gesture back: if a bottom-sheet menu (subtitles / audio / speed / scaling) is open,
    // back just dismisses it and keeps the player up. Only when no sheet is open does back fall through
    // to the default (pop the modal = close the player), matching the prior behaviour.
    protected override bool OnBackButtonPressed()
    {
        if (_sheetOverlay.IsVisible)
        {
            CloseSheet();
            return true;   // consumed — don't close the player
        }
        return base.OnBackButtonPressed();
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
        // Auto-hide while playing (TV included now): a remote re-summons the chrome via the wake-key
        // hook (MainActivity.DispatchKeyEvent → OnTvWakeKey), so hidden controls are no longer a dead end.
        if (_player.IsPlaying && !_sheetOverlay.IsVisible)
            _hideTimer.Start();
    }

    // Remote key while a TV player is foreground (called from MainActivity on the UI thread). When the
    // chrome has auto-hidden, the first D-pad/OK press just brings it back (focused on play) and is
    // swallowed; otherwise it keeps the chrome up a little longer and falls through to the focused control.
    private bool OnTvWakeKey()
    {
        if (_sheetOverlay.IsVisible) { RestartHideTimer(); return false; }
        if (!_controls.IsVisible)
        {
            ShowControls();
            try { _playPause.Focus(); } catch { /* not realized */ }
            return true;
        }
        RestartHideTimer();
        return false;
    }

    private void StopHideTimer() => _hideTimer.Stop();

    // Toggle the connecting/buffering overlay. The spinner is stopped when hidden so it doesn't animate
    // off-screen (and burn cycles) during playback.
    private void SetLoading(bool on)
    {
        _loading.IsVisible = on;
        _spinner.IsRunning = on;
        // Hide the centre transport while the spinner is up so the play/pause glyph doesn't sit under it.
        _transport.IsVisible = !on;
    }

    // ── Bottom sheets ───────────────────────────────────────────────────────────
    private void OpenSheet(string title, View content)
    {
        _sheetTitle.Text = title;
        _sheetContent.Content = content;
        _sheetOverlay.IsVisible = true;
        _controls.IsVisible = true;
        StopHideTimer();
        // On TV, land the remote on the first row so the D-pad has somewhere to start.
        if (_isTv) FocusFirstRow();
    }

    // Focus the first selectable row in the open sheet (TV only). Delayed so the sheet's native views are
    // realized before we try to focus; best-effort — a missed focus just means the first D-pad press lands it.
    private void FocusFirstRow()
    {
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), () =>
        {
            try { _sheetContent.GetVisualTreeDescendants().OfType<Button>().FirstOrDefault()?.Focus(); }
            catch { /* not realized yet */ }
        });
    }

    private void CloseSheet()
    {
        _sheetOverlay.IsVisible = false;
        ShowControls();
    }

    // A single tappable list row with a selection check. onTap decides whether to close the sheet.
    // selected = the active track (purple + checkmark). viewing = this language's tracks are shown in the
    // middle column but nothing's been picked yet — give it a bold/highlighted look so a language tap reads
    // as "selected to browse" even before a track is chosen, without the checkmark that means "active".
    private View Row(string text, bool selected, Action onTap, bool viewing = false)
    {
        var emphasise = selected || viewing;
        // TV: a Grid+TapGestureRecognizer isn't reachable by the D-pad, so use a Button (gets the native
        // focus highlight + activates on OK). The check is folded into the text since a Button hosts only text.
        if (_isTv)
        {
            var btn = new Button
            {
                Text = (selected ? "✓  " : "") + text,
                TextColor = emphasise ? Accent : Colors.White,
                FontAttributes = emphasise ? FontAttributes.Bold : FontAttributes.None,
                BackgroundColor = Colors.Transparent,
                BorderWidth = 0,
                CornerRadius = 0,
                FontSize = 16,
                Padding = new Thickness(16, 12),
                HorizontalOptions = LayoutOptions.Fill,
            };
            btn.Clicked += (_, _) => onTap();
            return btn;
        }

        var label = new Label
        {
            Text = text,
            TextColor = emphasise ? Accent : Colors.White,
            FontAttributes = emphasise ? FontAttributes.Bold : FontAttributes.None,
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
            // Faint fill behind the browsed-but-not-active language so the tap registers visually.
            BackgroundColor = viewing && !selected ? Color.FromRgba(255, 255, 255, 18) : Colors.Transparent,
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
                    () => { _userPickedAudio = true; try { _player.SetAudioTrack(id); } catch { } CloseSheet(); }));
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
            Row("Crop", _scaleMode == ScaleMode.Crop, () => ApplyScale(ScaleMode.Crop)),
            Row("Fill", _scaleMode == ScaleMode.Fill, () => ApplyScale(ScaleMode.Fill)),
            Row("16:9", _scaleMode == ScaleMode.SixteenNine, () => ApplyScale(ScaleMode.SixteenNine)),
            Row("4:3", _scaleMode == ScaleMode.FourThree, () => ApplyScale(ScaleMode.FourThree)),
        };
        OpenSheet("Video scaling", ScrollList(rows));
    }

    private void ApplyScale(ScaleMode mode)
    {
        _scaleMode = mode;
        try
        {
            // Aspect and crop are independent VLC knobs — always reset both so modes don't stack.
            switch (mode)
            {
                case ScaleMode.Fit:
                    _player.AspectRatio = null;
                    _player.CropGeometry = null;
                    break;
                case ScaleMode.Crop:
                    // Zoom until the screen is covered, trimming the overflow edges — no distortion.
                    // Cropping the source to the screen's aspect ratio does exactly that.
                    _player.AspectRatio = null;
                    _player.CropGeometry = SurfaceRatio();
                    break;
                case ScaleMode.Fill:
                    // Stretch the picture to the screen's aspect (fills fully; may distort, keeps all
                    // content). "Crop" above is the no-distortion zoom-to-fill alternative.
                    _player.AspectRatio = SurfaceRatio();
                    _player.CropGeometry = null;
                    break;
                case ScaleMode.SixteenNine:
                    _player.AspectRatio = "16:9";
                    _player.CropGeometry = null;
                    break;
                case ScaleMode.FourThree:
                    _player.AspectRatio = "4:3";
                    _player.CropGeometry = null;
                    break;
            }
            _player.Scale = 0; // auto-fit to the view
        }
        catch { /* not ready */ }
        CloseSheet();
    }

    // Ratio of the actual video surface, so Crop/Fill cover exactly the screen we draw on (including the
    // cutout area). Falls back to the physical display ratio if the native view hasn't been laid out yet.
    private string? SurfaceRatio()
    {
#if ANDROID
        if (_videoView.Handler?.PlatformView is global::Android.Views.View v && v.Width > 0 && v.Height > 0)
            return $"{v.Width}:{v.Height}";
#endif
        var d = DeviceDisplay.Current.MainDisplayInfo;
        var w = (int)Math.Max(d.Width, d.Height);
        var h = (int)Math.Min(d.Width, d.Height);
        return h > 0 ? $"{w}:{h}" : null;
    }

    // One selectable subtitle option, whether it's an in-file track or an external slave.
    private readonly record struct SubOption(string Lang, string Track, bool Selected, Action OnPick);

    // ── Subtitles: Stremio-style language · tracks · options columns ────────────
    private void OpenSubtitleSheet()
    {
        // Embedded (in-file) SPU tracks straight from libVLC. Capture the embedded id set on the first read —
        // before any external slave is attached — so a slave we add on selection (which libVLC then appends
        // to SpuDescription) isn't later mistaken for an in-file track.
        var spu = new List<(int Id, string Name)>();
        try
        {
            foreach (var t in _player.SpuDescription)
                if (t.Id != -1)
                    spu.Add((t.Id, string.IsNullOrWhiteSpace(t.Name) ? $"Track {t.Id}" : t.Name));
        }
        catch { /* tracks not parsed yet */ }
        _embeddedSpuIds ??= spu.Select(t => t.Id).ToHashSet();

        int currentSpu;
        try { currentSpu = _player.Spu; } catch { currentSpu = -1; }

        // libVLC names embedded SPU tracks by codec ("ASS"/"SRT"), not language. Pull the richer media-track
        // metadata (ISO language + the track's Name/Description element) so an in-file track shows under its
        // real language. Also grab the dominant audio language as a last-resort hint for untagged subs.
        var spuMeta = new Dictionary<int, (string? Lang, string? Desc)>();
        string? audioLang = null;
        try
        {
            var tracks = _player.Media?.Tracks;
            if (tracks != null)
                foreach (var track in tracks)
                {
                    if (track.TrackType == TrackType.Text)
                        spuMeta[track.Id] = (track.Language, track.Description);
                    else if (track.TrackType == TrackType.Audio && string.IsNullOrEmpty(audioLang)
                             && !string.IsNullOrWhiteSpace(track.Language))
                        audioLang = track.Language;
                }
        }
        catch { /* media not parsed yet — fall back to name parsing */ }

        // Unified list: in-file tracks + the external OpenSubtitles list (from the API, which carries real
        // languages). External subs attach on demand when picked (AddSlave select:true), so the picker shows
        // everything available regardless of what libVLC has lazily parsed.
        var options = new List<SubOption>();
        foreach (var t in spu.Where(t => _embeddedSpuIds.Contains(t.Id)))
        {
            var id = t.Id;
            spuMeta.TryGetValue(id, out var meta);
            var lang = ResolveEmbeddedLang(meta.Lang, meta.Desc, t.Name, audioLang);
            options.Add(new SubOption(
                lang, t.Name,
                _selectedExternalUrl is null && currentSpu == id,
                () => { _userPickedSub = true; try { _player.SetSpu(id); } catch { } _selectedExternalUrl = null; CloseSheet(); }));
        }
        foreach (var sub in _externalSubs)
        {
            var lang = FriendlyLang(sub.Language, sub.Label);
            var track = string.IsNullOrWhiteSpace(sub.Label) ? lang : sub.Label!;
            var url = sub.Url;
            options.Add(new SubOption(
                lang, track,
                _selectedExternalUrl == url,
                () => { _userPickedSub = true; AttachExternalSub(url); CloseSheet(); }));
        }

        var langs = options.Select(o => o.Lang).Distinct().ToList();

        // The language of the active subtitle (null when off). Drives the left-column checkmark — distinct
        // from _subLang, which is only the language whose tracks the middle column is currently *viewing*.
        var activeLang = options.Where(o => o.Selected).Select(o => o.Lang).FirstOrDefault();

        // Default the viewed language column to the active track's language, else the first available.
        if (_subLang is null || !langs.Contains(_subLang))
            _subLang = activeLang ?? langs.FirstOrDefault();

        // Left column: "Off" + one entry per language. Selecting a language just re-filters (keeps the sheet
        // open); selecting "Off" disables subtitles and closes. The checkmark tracks the ACTIVE language, so
        // turning subtitles off clears it instead of leaving the previously-viewed language ticked.
        var langRows = new List<View>
        {
            Row("Off", currentSpu == -1 && _selectedExternalUrl is null,
                () => { _userPickedSub = true; try { _player.SetSpu(-1); } catch { } _selectedExternalUrl = null; CloseSheet(); }),
        };
        foreach (var lang in langs)
        {
            var l = lang;
            langRows.Add(Row(l, l == activeLang, () => { _subLang = l; OpenSubtitleSheet(); }, viewing: l == _subLang));
        }

        // Middle column: tracks in the selected language. Selecting one applies + closes.
        var trackRows = options
            .Where(o => o.Lang == _subLang)
            .Select(o => Row(o.Track, o.Selected, o.OnPick))
            .ToList();
        if (trackRows.Count == 0)
            trackRows.Add(Row(options.Count == 0 ? "No subtitles available" : "—", false, () => { }));

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

    // Common ISO 639-1 / 639-2 subtitle language codes → display names (the API hands us a code like "en"
    // and a label like "English 2"; the code gives the clean Language-column heading).
    private static readonly Dictionary<string, string> LangNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["eng"] = "English",
        ["ja"] = "Japanese", ["jpn"] = "Japanese", ["jp"] = "Japanese",
        ["ar"] = "Arabic", ["ara"] = "Arabic",
        ["es"] = "Spanish", ["spa"] = "Spanish",
        ["fr"] = "French", ["fra"] = "French", ["fre"] = "French",
        ["de"] = "German", ["deu"] = "German", ["ger"] = "German",
        ["el"] = "Greek", ["ell"] = "Greek", ["gre"] = "Greek",
        ["it"] = "Italian", ["ita"] = "Italian",
        ["pt"] = "Portuguese", ["por"] = "Portuguese",
        ["ru"] = "Russian", ["rus"] = "Russian",
        ["zh"] = "Chinese", ["zho"] = "Chinese", ["chi"] = "Chinese",
        ["ko"] = "Korean", ["kor"] = "Korean",
        ["pl"] = "Polish", ["pol"] = "Polish",
        ["tr"] = "Turkish", ["tur"] = "Turkish",
        ["nl"] = "Dutch", ["nld"] = "Dutch", ["dut"] = "Dutch",
        ["id"] = "Indonesian", ["ind"] = "Indonesian",
        ["hi"] = "Hindi", ["hin"] = "Hindi",
        ["th"] = "Thai", ["tha"] = "Thai",
        ["vi"] = "Vietnamese", ["vie"] = "Vietnamese",
    };

    // Friendly language for the Language column: map the API's language code, fall back to the label with any
    // trailing index stripped ("English 2" → "English"), then to the raw code.
    private static string FriendlyLang(string? code, string? label)
    {
        var c = (code ?? "").Trim();
        if (c.Length > 0 && LangNames.TryGetValue(c, out var name)) return name;
        var lbl = (label ?? "").Trim();
        if (lbl.Length > 0)
        {
            var stripped = System.Text.RegularExpressions.Regex.Replace(lbl, @"\s*\d+$", "").Trim();
            return stripped.Length > 0 ? stripped : lbl;
        }
        return c.Length > 0 ? c.ToUpperInvariant() : "Subtitle";
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

    // Resolve the friendly language for an embedded SPU track, mirroring the cf-mkv-extractor worker's
    // dual-signal approach for the common "Language=und" case:
    //   1. a real ISO language tag wins;
    //   2. else scan the track Name/Description for a language word or 3-letter code (fansubs often leave
    //      Language=und but name the track "English [Group]" / "Japanese");
    //   3. else fall back to the audio track's language — untagged in-file subs almost always match the
    //      programme's spoken language (e.g. Japanese for anime);
    //   4. else give up and show the codec/name parse, as before.
    private static string ResolveEmbeddedLang(string? code, string? desc, string? spuName, string? audioLang)
    {
        var c = (code ?? "").Trim();
        if (c.Length > 0 && !c.Equals("und", StringComparison.OrdinalIgnoreCase)
            && LangNames.TryGetValue(c, out var byCode)) return byCode;

        var byName = LangFromName(desc) ?? LangFromName(spuName);
        if (byName != null) return byName;

        var a = (audioLang ?? "").Trim();
        if (a.Length > 0 && !a.Equals("und", StringComparison.OrdinalIgnoreCase)
            && LangNames.TryGetValue(a, out var byAudio)) return byAudio;

        return LangOf(spuName ?? "");
    }

    // Look for a language in a free-text track name: a display name as a whole word ("english"/"japanese"),
    // else a 3-letter ISO code as a standalone token ("eng"/"jpn"). 2-letter codes are skipped — too short,
    // they false-match inside ordinary words.
    private static string? LangFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.ToLowerInvariant();
        foreach (var kv in LangNames)
            if (System.Text.RegularExpressions.Regex.IsMatch(n, $@"\b{kv.Value.ToLowerInvariant()}\b")) return kv.Value;
        foreach (var kv in LangNames)
            if (kv.Key.Length == 3 && System.Text.RegularExpressions.Regex.IsMatch(n, $@"\b{kv.Key.ToLowerInvariant()}\b")) return kv.Value;
        return null;
    }

    // ── Event marshalling + formatting ──────────────────────────────────────────
    private void OnPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e)
        => Dispatcher.Dispatch(() => { if (!_seeking) _seek.Value = Math.Clamp(e.Position, 0, 1); UpdateTimes(); });

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        => Dispatcher.Dispatch(UpdateTimes);

    private void UpdateTimes()
    {
        // While taps accumulate, show the pending target so the clock tracks the scrub, not the decoder.
        _position.Text = Format(_seekPending ? _pendingSeekTarget : _player.Time);
        _duration.Text = Format(_player.Length);
        UpdateSkipButton();
    }

    // Show / hide the skip corner button based on the current position. Driven off the same
    // position ticks as the clock, so it appears within ~1s of entering a band. No early-out on the
    // raw API bands — chapter fallback may add bands after the constructor; ActiveBand returns null
    // when nothing matches.
    private void UpdateSkipButton()
    {
        var t = _player.Time / 1000.0;
        var band = ActiveBand(t);
        if (band is null)
        {
            if (_skipButton.IsVisible) _skipButton.IsVisible = false;
            return;
        }
        _skipTarget = band.Value.End + 1;       // land just past the band so we don't re-enter it
        _skipButton.Text = band.Value.Label;
        if (!_skipButton.IsVisible) _skipButton.IsVisible = true;
    }

    // The active intro/recap/outro band at time t (seconds), or null. Reads the effective bands
    // (API + chapter fallback). The −0.5 keeps the button from flashing for a frame at the boundary.
    private (string Label, double End)? ActiveBand(double t)
    {
        if (_effIntro is { } i && t >= i.Start && t < i.End - 0.5) return ("Skip Intro  ⏭", i.End);
        if (_effRecap is { } r && t >= r.Start && t < r.End - 0.5) return ("Skip Recap  ⏭", r.End);
        if (_effOutro is { } o && t >= o.Start && t < o.End - 0.5) return ("Skip Outro  ⏭", o.End);
        return null;
    }

    // Phone-only fallback: when the API (AniSkip/introdb) didn't supply a band type, derive it from the
    // file's embedded chapter markers (e.g. "Intro"/"Recap"/"Credits") — common in scene/Blu-ray rips.
    // One-shot, read after the first frame since chapters parse a beat behind Playing. The API always wins
    // per type; chapters only fill the null ones.
    private void TryApplyChapterFallback()
    {
        if (_chaptersTried) return;
        _chaptersTried = true;
        try
        {
            var titleIdx = _player.Title;
            if (titleIdx < 0) titleIdx = 0;
            var chapters = _player.FullChapterDescriptions(titleIdx);
            if (chapters is null || chapters.Length == 0) return;
            for (int k = 0; k < chapters.Length; k++)
            {
                var ch = chapters[k];
                var startSec = ch.TimeOffset / 1000.0;
                var endSec = (k + 1 < chapters.Length)
                    ? chapters[k + 1].TimeOffset / 1000.0
                    : (ch.Duration > 0 ? startSec + ch.Duration / 1000.0 : startSec + 90);
                switch (ClassifyChapter(ch.Name))
                {
                    case "intro" when _effIntro is null: _effIntro = new SkipMark(startSec, endSec); break;
                    case "recap" when _effRecap is null: _effRecap = new SkipMark(startSec, endSec); break;
                    case "outro" when _effOutro is null: _effOutro = new SkipMark(startSec, endSec); break;
                }
            }
            UpdateSkipButton();   // a band we're already inside should show immediately
        }
        catch { /* chapters unavailable on this source — API-only */ }
    }

    // Classify a chapter title into a skip band. Recap is checked before ED so "previously" wins;
    // \bop\b / \bed\b are narrow word matches so they don't catch "open"/"edit".
    private static string? ClassifyChapter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.ToLowerInvariant();
        if (Regex.IsMatch(n, @"\b(intro|opening|op)\b")) return "intro";
        if (Regex.IsMatch(n, @"\b(recap|previously|last time)\b")) return "recap";
        if (Regex.IsMatch(n, @"\b(outro|credits?|ending|ed|end card)\b")) return "outro";
        return null;
    }

    private void OnSkipClicked(object? sender, EventArgs e)
    {
        try
        {
            var len = _player.Length;                 // ms; 0 until known
            var targetMs = (long)(_skipTarget * 1000);
            // An outro usually runs to the episode end, so End+1 lands at/after EOF — which libVLC can't
            // seek to (it buffers then snaps back to the same spot, the reported bug). Clamp to ~1.5s before
            // the end so the seek lands on real data and playback rolls into the natural end (→ EndReached).
            if (len > 0 && targetMs > len - 1500) targetMs = len - 1500;
            if (targetMs < 0) targetMs = 0;
            _player.Time = targetMs;
        }
        catch { /* not seekable yet */ }
        _skipButton.IsVisible = false;
    }

    // Read the decoded video size from libVLC (available a beat after the first frame) and show a
    // resolution / "4K" badge in the top bar. Confirms whether a 4K source is actually decoding at 4K.
    private void ShowResolution()
    {
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(900), () =>
        {
            try
            {
                uint w = 0, h = 0;
                if (_player.Size(0, ref w, ref h) && h > 0)
                    _resLabel.Text = ResLabel((int)w, (int)h);
            }
            catch { /* size not exposed yet — leave the badge blank */ }
        });
    }

    private static string ResLabel(int w, int h)
    {
        if (w >= 3840 || h >= 2160) return "4K";
        if (h >= 1440) return "1440p";
        if (h >= 1080) return "1080p";
        if (h >= 720) return "720p";
        return h > 0 ? $"{h}p" : "";
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
        // TV: a HorizontalStackLayout + TapGestureRecognizer isn't reachable by the D-pad (the same
        // reason Row() switches to a Button on TV), which is why the bottom menus — Subtitles /
        // Audio / Speed / Scaling — couldn't be focused with a remote. Use a real Button: it gets the
        // native focus highlight and activates on OK, with the glyph as a leading font icon so the
        // look matches the touch row.
        if (_isTv)
        {
            var btn = new Button
            {
                Text = label,
                ImageSource = new FontImageSource { Glyph = icon, FontFamily = IconFont, Color = Colors.White, Size = 18 },
                ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 6),
                TextColor = Colors.White,
                FontSize = 13,
                BackgroundColor = Colors.Transparent,
                BorderWidth = 0,
                CornerRadius = 0,
                Padding = new Thickness(6, 6),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };
            btn.Clicked += (_, _) => onTap();
            return btn;
        }

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
