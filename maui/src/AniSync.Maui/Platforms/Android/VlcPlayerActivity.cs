using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AniSync.Client.Services;
using LibVLCSharp.Shared;
using AndroidVideoView = LibVLCSharp.Platforms.Android.VideoView;
// MAUI's implicit usings pull in Microsoft.Maui.Graphics.Color, which collides with Android.Graphics.Color.
using AndroidColor = Android.Graphics.Color;

namespace AniSync;

/// <summary>
/// Native full-screen player Activity for Android TV. Background: the embedded LibVLCSharp.MAUI VideoView —
/// hosted in a MAUI modal DialogFragment window — could never present a 4K frame on the TV (decoded, never
/// shown), across every decode/direct-rendering/vout combination, while standalone/Stremio VLC plays the same
/// file fine. The one untested structural difference was the host window: VLC-Android renders its SurfaceView
/// in a real Activity, we used a DialogFragment. So on TV we host libVLCSharp's Android VideoView (itself a
/// SurfaceView) in a genuine Activity here, reusing the app's configured LibVLC and reporting progress/end
/// back through the request callbacks so resume + scrobble still work. Phones keep the MAUI player.
/// Controls are intentionally minimal (D-pad OK = play/pause, left/right = seek, Back = exit) plus the same
/// on-screen decode diagnostic (rendered as a native TextView, so it can't be blamed for starving MAUI).
/// </summary>
[Activity(
    Label = "AniSync Player",
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    Exported = false)]
internal sealed class VlcPlayerActivity : Activity
{
    private const string BuildTag = "dbg8";

    // Handed over by VlcMediaPlayer just before launching (same process). We reuse the app's configured
    // LibVLC singleton rather than building a second one, and keep the request for its resume position +
    // progress/end callbacks. Cleared in OnCreate so a later launch can't pick up a stale request.
    internal static LibVLC? PendingLibVlc;
    internal static PlaybackRequest? PendingRequest;

    public static void Launch(Context context, LibVLC libVlc, PlaybackRequest request)
    {
        PendingLibVlc = libVlc;
        PendingRequest = request;
        var intent = new Intent(context, typeof(VlcPlayerActivity));
        if (context is not Activity) intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    private MediaPlayer? _player;
    private AndroidVideoView? _videoView;
    private TextView? _diag;
    private Handler? _handler;
    private Action? _tick;
    private long _prevDec, _prevShown, _prevLost;
    private bool _pausedForBackground;
    private bool _released;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var libVlc = PendingLibVlc;
        var request = PendingRequest;
        PendingLibVlc = null;
        PendingRequest = null;
        if (libVlc is null || request is null) { Finish(); return; }

        EnterImmersive();

        var root = new FrameLayout(this);
        root.SetBackgroundColor(AndroidColor.Black);

        _videoView = new AndroidVideoView(this);
        root.AddView(_videoView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _diag = new TextView(this);
        _diag.SetTextColor(AndroidColor.Lime);
        _diag.SetBackgroundColor(AndroidColor.Argb(170, 0, 0, 0));
        _diag.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        _diag.SetTextSize(ComplexUnitType.Sp, 11);
        _diag.SetPadding(16, 12, 16, 12);
        _diag.Text = "diag…";
        root.AddView(_diag, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Left | GravityFlags.CenterVertical });

        SetContentView(root);

        _player = new MediaPlayer(libVlc) { EnableHardwareDecoding = true };
        _videoView.MediaPlayer = _player;

        // Resume + scrobble flow back through the same callbacks the MAUI page uses (libVLC raises these on
        // its own thread; the handlers marshal to the renderer). Capture the player locally and swallow faults.
        var player = _player;
        if (request.OnProgress is not null)
            player.TimeChanged += (_, e) => { try { request.OnProgress(e.Time / 1000.0, player.Length / 1000.0); } catch { } };
        if (request.OnEnded is not null)
            player.EndReached += (_, _) => { try { request.OnEnded(); } catch { } };

        // Dispose the wrapper at OnCreate exit (after Play) — the player retains the native media, matching
        // VlcMediaPlayer's pattern.
        using var media = new Media(libVlc, new Uri(request.Url));
        if (request.ResumeSeconds is > 0)
            media.AddOption($":start-time={(int)request.ResumeSeconds.Value}");
        player.Media = media;
        player.Play();

        StartDiagnostics();
    }

    private void EnterImmersive()
    {
        var window = Window;
        if (window is null) return;
        window.AddFlags(WindowManagerFlags.KeepScreenOn);
#pragma warning disable CA1422 // legacy immersive flags, still honoured on TV OEM skins
        window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
            SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen |
            SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky);
#pragma warning restore CA1422
    }

    // ── Minimal D-pad transport ─────────────────────────────────────────────────
    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        switch (keyCode)
        {
            case Keycode.DpadCenter:
            case Keycode.Enter:
            case Keycode.ButtonA:
            case Keycode.MediaPlayPause:
                try { _player?.SetPause(_player.IsPlaying); } catch { }
                return true;
            case Keycode.DpadLeft:
            case Keycode.MediaRewind:
                Seek(-10_000);
                return true;
            case Keycode.DpadRight:
            case Keycode.MediaFastForward:
                Seek(+10_000);
                return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    private void Seek(long deltaMs)
    {
        try
        {
            if (_player is null) return;
            var target = _player.Time + deltaMs;
            _player.Time = target < 0 ? 0 : target;
        }
        catch { }
    }

    // ── On-screen decode diagnostic (native TextView, polled once a second) ─────
    private void StartDiagnostics()
    {
        _handler = new Handler(Looper.MainLooper!);
        _tick = () =>
        {
            UpdateDiagnostics();
            if (!_released) _handler?.PostDelayed(_tick!, 1000);
        };
        _handler.PostDelayed(_tick, 1000);
    }

    private void UpdateDiagnostics()
    {
        if (_player is null || _diag is null) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("ACT ").Append(BuildTag).Append("  ").Append(_player.State).Append("  hw:on");
            uint w = 0, h = 0;
            if (_player.Size(0, ref w, ref h) && h > 0) sb.Append("  ").Append(w).Append('x').Append(h);
            sb.Append('\n');
            var media = _player.Media;
            if (media is not null)
            {
                var s = media.Statistics;
                long dec = s.DecodedVideo, shown = s.DisplayedPictures, lost = s.LostPictures;
                sb.Append("dec ").Append(dec).Append(" (+").Append(dec - _prevDec).Append(")  shown ")
                  .Append(shown).Append(" (+").Append(shown - _prevShown).Append(")\n");
                sb.Append("lostpic ").Append(lost).Append(" (+").Append(lost - _prevLost).Append(")  demux ")
                  .Append((s.DemuxBitrate * 8000f).ToString("0")).Append("kb/s\n");
                _prevDec = dec; _prevShown = shown; _prevLost = lost;
            }
            sb.Append("t ").Append(_player.Time / 1000).Append("s / ").Append(_player.Length / 1000).Append('s');
            _diag.Text = sb.ToString();
        }
        catch { }
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────────
    protected override void OnPause()
    {
        base.OnPause();
        try { if (_player?.IsPlaying == true) { _player.SetPause(true); _pausedForBackground = true; } } catch { }
    }

    protected override void OnResume()
    {
        base.OnResume();
        EnterImmersive();
        try { if (_pausedForBackground) { _player?.SetPause(false); _pausedForBackground = false; } } catch { }
    }

    protected override void OnDestroy()
    {
        Release();
        base.OnDestroy();
    }

    private void Release()
    {
        if (_released) return;
        _released = true;
        try { _handler?.RemoveCallbacksAndMessages(null); } catch { }
        try { if (_videoView is not null) _videoView.MediaPlayer = null; } catch { }
        try { _player?.Stop(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;
        try { _videoView?.Dispose(); } catch { }
        _videoView = null;
        // The LibVLC instance is the app's DI singleton — do NOT dispose it here.
    }
}
