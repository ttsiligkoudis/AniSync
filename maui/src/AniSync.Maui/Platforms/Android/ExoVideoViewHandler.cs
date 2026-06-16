using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.UI;
using Microsoft.Maui.Handlers;
using AndroidUri = Android.Net.Uri;

namespace AniSync.Maui;

/// <summary>
/// Android handler for <see cref="ExoVideoView"/>: builds and owns a Media3 <c>ExoPlayer</c> hosted in a
/// <c>PlayerView</c>. PlayerView's built-in controls cover play/seek and a settings menu for audio/subtitle
/// track selection and playback speed (all D-pad friendly), so the chrome is free. We add: sideloaded
/// external subtitles, preferred audio/subtitle language preselection, resume, progress/ended callbacks,
/// background pause/resume, and a MENU-key video-scaling cycle.
/// </summary>
public sealed class ExoVideoViewHandler : ViewHandler<ExoVideoView, PlayerView>
{
    public ExoVideoViewHandler() : base(ViewMapper) { }

    private IExoPlayer? _player;
    private Android.OS.Handler? _ticker;
    private Action? _tick;
    private bool _ticking;
    private bool _ended;
    private bool _wasPlayingBeforeBackground;
    private int _scaleIndex;

    // Controls we've already given a TV focus highlight (so we set each view's selector exactly once).
    private readonly HashSet<Android.Views.View> _highlighted = new();

    // Player.STATE_ENDED. Using the literal avoids depending on the exact binding constant name.
    private const int StateEnded = 4;

    // Cycled by the MENU key: Fit (letterbox), Zoom (fill + crop), Fill (stretch).
    private static readonly int[] ResizeModes =
    {
        AspectRatioFrameLayout.ResizeModeFit,
        AspectRatioFrameLayout.ResizeModeZoom,
        AspectRatioFrameLayout.ResizeModeFill,
    };
    private static readonly string[] ScaleLabels = { "Fit", "Zoom", "Fill" };

    protected override PlayerView CreatePlatformView()
    {
        var view = new ScalingPlayerView(Context!)
        {
            KeepScreenOn = true,
        };
        view.SetShowSubtitleButton(true);   // CC button → toggle/select text tracks (incl. our sideloaded subs)
        view.SetBackgroundColor(Android.Graphics.Color.Black);
        view.OnCycleScale = CycleScale;
        // ExoPlayer's default control buttons use a borderless ripple background, which shows on touch but
        // gives NO visible focus highlight under D-pad navigation — on a TV you can click buttons but can't
        // see which one is selected. Give each focusable control an explicit focus-state background so the
        // selected control is clearly outlined. The controls are inflated with the view, but lay out a beat
        // later, so apply after a short delay (and again, in case any button appears when first shown).
        view.PostDelayed(() => ApplyTvFocusHighlights(view), 600);
        view.PostDelayed(() => ApplyTvFocusHighlights(view), 1500);
        return view;
    }

    protected override void ConnectHandler(PlayerView platformView)
    {
        base.ConnectHandler(platformView);

        var request = VirtualView.Request;
        VirtualView.PauseForBackgroundRequested += OnPauseForBackground;
        VirtualView.ResumeFromBackgroundRequested += OnResumeFromBackground;

        // The Java ExoPlayer interface binds to C# as IExoPlayer, so its nested Builder is IExoPlayer.Builder.
        var player = new IExoPlayer.Builder(Context!).Build()!;
        _player = player;
        platformView.Player = player;

        // Build the MediaItem, sideloading any external subtitle tracks (the proxied OpenSubtitles URLs from
        // the API) so they appear in the CC button / settings menu alongside the file's embedded tracks.
        var builder = new MediaItem.Builder().SetUri(request.Url);
        if (request.Subtitles is { Count: > 0 } subs)
        {
            var configs = new List<MediaItem.SubtitleConfiguration>(subs.Count);
            foreach (var s in subs)
            {
                var cfg = new MediaItem.SubtitleConfiguration.Builder(AndroidUri.Parse(s.Url)!)
                    .SetMimeType(GuessSubtitleMime(s.Url))!
                    .SetLabel(s.Label)!;
                if (!string.IsNullOrWhiteSpace(s.Language))
                    cfg = cfg.SetLanguage(s.Language)!;
                configs.Add(cfg.Build());
            }
            builder.SetSubtitleConfigurations(configs);
        }
        var item = builder.Build();

        // Preselect audio + subtitle language: anime dual-audio releases often default to Japanese, so honour
        // the preferred audio language; subtitles default to English like the web head.
        var audioLang = string.IsNullOrWhiteSpace(request.PreferredAudioLanguage) ? null : request.PreferredAudioLanguage;
        var textLang = string.IsNullOrWhiteSpace(request.PreferredSubtitleLanguage) ? "en" : request.PreferredSubtitleLanguage;
        var tsp = player.TrackSelectionParameters!.BuildUpon();
        if (audioLang is not null) tsp.SetPreferredAudioLanguage(audioLang);
        tsp.SetPreferredTextLanguage(textLang);
        player.TrackSelectionParameters = tsp.Build();

        // Resume via the start-position overload (no need to wait for a "ready" callback to seek).
        var startMs = request.ResumeSeconds is > 0 ? (long)(request.ResumeSeconds.Value * 1000) : 0L;
        player.SetMediaItem(item, startMs);
        player.Prepare();
        player.PlayWhenReady = true;

        StartTicker();
    }

    protected override void DisconnectHandler(PlayerView platformView)
    {
        StopTicker();
        if (VirtualView is not null)
        {
            VirtualView.PauseForBackgroundRequested -= OnPauseForBackground;
            VirtualView.ResumeFromBackgroundRequested -= OnResumeFromBackground;
        }
        platformView.Player = null;
        try { _player?.Release(); } catch { /* already released */ }
        _player = null;
        base.DisconnectHandler(platformView);
    }

    // Poll position once a second on the main thread: drives resume persistence + scrobble via OnProgress,
    // and fires OnEnded once when playback completes. Polling (rather than a Player.Listener) keeps us off the
    // version-specific listener-interface binding.
    private void StartTicker()
    {
        _ticking = true;
        _ticker = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
        _tick = () =>
        {
            if (!_ticking) return;
            var p = _player;
            if (p is not null)
            {
                try
                {
                    long pos = p.CurrentPosition, dur = p.Duration;
                    if (dur > 0) VirtualView?.Request.OnProgress?.Invoke(pos / 1000.0, dur / 1000.0);
                    if (!_ended && p.PlaybackState == StateEnded)
                    {
                        _ended = true;
                        VirtualView?.Request.OnEnded?.Invoke();
                    }
                }
                catch { /* player torn down between ticks */ }
            }
            if (_ticking) _ticker?.PostDelayed(_tick!, 1000);
        };
        _ticker.PostDelayed(_tick, 1000);
    }

    private void StopTicker()
    {
        _ticking = false;
        _ticker = null;
        _tick = null;
    }

    private void CycleScale()
    {
        if (PlatformView is null) return;
        _scaleIndex = (_scaleIndex + 1) % ResizeModes.Length;
        PlatformView.ResizeMode = ResizeModes[_scaleIndex];
        Toast.MakeText(Context, ScaleLabels[_scaleIndex], ToastLength.Short)?.Show();
    }

    // Walk the PlayerView tree and give every focusable control (the play/seek/CC/settings ImageButtons) a
    // background that highlights the focused (or pressed) state, so D-pad navigation is visible on TV. Skips
    // the seek bar (a custom view, not an ImageView/TextView) which draws its own focused scrubber. Each view
    // is highlighted once — a Drawable can only back one view, so we don't share instances.
    private void ApplyTvFocusHighlights(Android.Views.View? view)
    {
        if (view is null) return;
        if (view is Android.Views.ViewGroup group)
            for (int i = 0; i < group.ChildCount; i++)
                ApplyTvFocusHighlights(group.GetChildAt(i));

        if ((view is ImageView or Android.Widget.TextView) && view.Focusable && view.Clickable && _highlighted.Add(view))
            view.Background = BuildFocusSelector(view.Context!);
    }

    private static Android.Graphics.Drawables.Drawable BuildFocusSelector(Context ctx)
    {
        var density = ctx.Resources?.DisplayMetrics?.Density ?? 1f;
        var highlight = new Android.Graphics.Drawables.GradientDrawable();
        highlight.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
        highlight.SetColor(Android.Graphics.Color.Argb(70, 255, 255, 255)); // translucent fill
        highlight.SetCornerRadius(8 * density);
        highlight.SetStroke((int)(2 * density), Android.Graphics.Color.White); // crisp focus ring

        var selector = new Android.Graphics.Drawables.StateListDrawable();
        selector.AddState(new[] { Android.Resource.Attribute.StateFocused }, highlight);
        selector.AddState(new[] { Android.Resource.Attribute.StatePressed }, highlight);
        selector.AddState(new[] { Android.Resource.Attribute.StateSelected }, highlight);
        selector.AddState(System.Array.Empty<int>(),
            new Android.Graphics.Drawables.ColorDrawable(Android.Graphics.Color.Transparent));
        return selector;
    }

    private void OnPauseForBackground()
    {
        var p = _player;
        if (p is null) return;
        _wasPlayingBeforeBackground = p.PlayWhenReady;
        p.PlayWhenReady = false;
    }

    private void OnResumeFromBackground()
    {
        if (_wasPlayingBeforeBackground && _player is { } p) p.PlayWhenReady = true;
    }

    // Media3 needs an explicit MIME type for sideloaded subtitle tracks. Guess from the URL; default to SubRip
    // (most OpenSubtitles downloads are .srt, and the proxied URLs often carry no extension).
    private static string GuessSubtitleMime(string url)
    {
        var u = url.ToLowerInvariant();
        if (u.Contains(".vtt")) return "text/vtt";
        if (u.Contains(".ass") || u.Contains(".ssa")) return "text/x-ssa";
        if (u.Contains(".ttml") || u.Contains(".dfxp")) return "application/ttml+xml";
        return "application/x-subrip";
    }

    // PlayerView subclass that catches the MENU key to cycle video scaling. Overriding DispatchKeyEvent
    // catches it before the focused child controls consume D-pad input, so scaling works whether or not the
    // chrome is showing.
    private sealed class ScalingPlayerView : PlayerView
    {
        public Action? OnCycleScale;
        public ScalingPlayerView(Context context) : base(context) { }

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (e is { Action: KeyEventActions.Down, KeyCode: Keycode.Menu })
            {
                OnCycleScale?.Invoke();
                return true;
            }
            return base.DispatchKeyEvent(e);
        }
    }
}
