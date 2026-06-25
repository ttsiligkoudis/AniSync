using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.UI;
using Microsoft.Maui.Handlers;
using AniSync.Client.Services;
using AndroidUri = Android.Net.Uri;

namespace AniSync.Maui;

/// <summary>
/// Android handler for <see cref="ExoVideoView"/>: builds and owns a Media3 <c>ExoPlayer</c> hosted in a
/// <c>PlayerView</c>. PlayerView's built-in controls cover play/seek and a settings menu for audio/subtitle
/// track selection and playback speed (all D-pad friendly), so the chrome is free. We add: sideloaded
/// external subtitles, preferred audio/subtitle language preselection, resume, progress/ended callbacks,
/// background pause/resume, −30s/+30s seek buttons, an on-screen video-scaling button (Fit/Zoom/Fill, also on
/// the MENU key), and a TV focus highlight on the controls and inside the gear/CC pop-up menus.
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
    private Android.Widget.ImageButton? _aspectButton;

    // Whether we engaged the tone-map (GL effects) pipeline for this playback (phones, not TV), and whether
    // the one-time HDR "look" boost has been applied yet. Media3's default HDR→SDR tone-map renders dimmer
    // and less saturated than libVLC, so once we confirm the stream is actually HDR we push the tone-mapped
    // result brighter/punchier/more vivid to match. Gated to HDR only — boosting already-graded SDR would
    // over-brighten/over-saturate it.
    private bool _toneMapEnabled;
    private bool _lookApplied;

    // HDR "look" knobs, applied (in order) on top of the SDR tone-map for HDR streams. Tunable: raise/lower
    // to taste vs libVLC. Brightness is additive [-1,1]; Contrast is [-1,1] (>0 = more punch); Saturation is
    // the HSL adjustment [-100,100] (>0 = more vivid).
    private const float ToneMapBrightness = 0.08f;
    private const float ToneMapContrast = 0.12f;
    private const float ToneMapSaturation = 28f;

    // AniSkip OP/ED bands (absolute seconds) + the "Skip Intro/Outro" overlay button. Shown by the
    // position ticker while inside a band; tapping (or D-pad OK) seeks just past it.
    private SkipMark? _skipIntro;
    private SkipMark? _skipOutro;
    private SkipMark? _skipRecap;
    private Android.Widget.Button? _skipButton;
    private long _skipTargetMs;

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
        // Wrap the context in a theme overlay that redirects the controls' selectable-item backgrounds to a
        // focus-highlighting drawable. This is what makes the focus visible INSIDE the gear/CC pop-up menus
        // (speed, audio, subtitle lists) — those list items live in a separate pop-up window we can't reach by
        // walking the view tree, but they inflate from this context's theme, so the override reaches them.
        var themed = new ContextThemeWrapper(Context!, Resource.Style.AniSync_ExoPlayerOverlay);
        var view = new ScalingPlayerView(themed)
        {
            KeepScreenOn = true,
        };
        view.SetShowSubtitleButton(true);       // CC button → toggle/select text tracks (incl. sideloaded subs)
        view.SetShowRewindButton(true);         // VLC-style −30s / +30s seek buttons (increment set on the player)
        view.SetShowFastForwardButton(true);
        // Media3's PlayerView defaults to show_buffering=never, so it never shows a spinner. Surface the
        // built-in buffering indicator while playing (initial load + mid-stream re-buffer), matching the
        // VLC head's connecting/buffering overlay.
        view.SetShowBuffering(PlayerView.ShowBufferingWhenPlaying);
        view.SetBackgroundColor(Android.Graphics.Color.Black);
        // Subtitles: match the libVLC look — white text with a dark outline and NO background box.
        // ExoPlayer's SubtitleView otherwise paints a translucent black box behind every cue.
        view.SubtitleView?.SetStyle(new AndroidX.Media3.UI.CaptionStyleCompat(
            Android.Graphics.Color.White.ToArgb(),
            Android.Graphics.Color.Transparent.ToArgb(),   // per-cue background box — removed
            Android.Graphics.Color.Transparent.ToArgb(),   // window background — removed
            AndroidX.Media3.UI.CaptionStyleCompat.EdgeTypeOutline,
            Android.Graphics.Color.Black.ToArgb(),
            null));
        view.OnCycleScale = CycleScale;
        // ExoPlayer's default control buttons use a borderless ripple background, which shows on touch but
        // gives NO visible focus highlight under D-pad navigation — on a TV you can click buttons but can't
        // see which one is selected. Give each focusable control an explicit focus-state background so the
        // selected control is clearly outlined, and inject the on-screen aspect-ratio button into the control
        // bar. The controls are inflated with the view but lay out a beat later, so do this after a short delay
        // (and again, in case any button appears only when the controls are first shown).
        view.PostDelayed(() => { ApplyTvFocusHighlights(view); EnsureAspectButton(); }, 600);
        view.PostDelayed(() => { ApplyTvFocusHighlights(view); EnsureAspectButton(); }, 1500);
        return view;
    }

    protected override void ConnectHandler(PlayerView platformView)
    {
        base.ConnectHandler(platformView);

        var request = VirtualView.Request;
        VirtualView.PauseForBackgroundRequested += OnPauseForBackground;
        VirtualView.ResumeFromBackgroundRequested += OnResumeFromBackground;

        // The Java ExoPlayer.Builder binds to C# as the top-level ExoPlayerBuilder; Build() returns IExoPlayer.
        // 30s seek increments drive the rewind / fast-forward buttons (VLC's −30 / +30).
        var player = new ExoPlayerBuilder(Context!)
            .SetSeekBackIncrementMs(30_000)
            .SetSeekForwardIncrementMs(30_000)
            .Build()!;
        _player = player;
        platformView.Player = player;

        // HDR → SDR tone-mapping (phones only). HDR streams render dark/washed on a phone because
        // ExoPlayer hardware-decodes without tone-mapping, whereas libVLC software-tonemaps (so it
        // looks brighter/correct). Routing playback through the effects (GL) pipeline makes Media3
        // tone-map HDR down to SDR by default — an empty effects list just enables that pipeline (this
        // needs the Xamarin.AndroidX.Media3.Effect package, which carries the GL DefaultVideoFrameProcessor;
        // without it SetVideoEffects throws "Could not find required lib-effect dependencies").
        // We tone-map even on HDR-capable phone panels: true HDR passthrough (window COLOR_MODE_HDR) was
        // tried and, on the panels tested, still looked dimmer than libVLC, so the tone-mapped SDR path is
        // the reliable match. The empty list tone-maps from the first frame (no dark flash) and is a no-op
        // for SDR; once the decoder confirms the stream is HDR, the ticker swaps in the brightness/saturation
        // boost (MaybeApplyHdrLook) so HDR matches libVLC's punchier look while SDR stays untouched.
        // Skip on TV: those panels pass HDR through natively and the GL path can choke on 4K there.
        // Best-effort: if a device can't set up the effects pipeline, fall back to direct rendering rather
        // than failing playback.
        // Microsoft.Maui.Devices.DeviceInfo, fully qualified — AndroidX.Media3.Common (used in this
        // file) also defines a DeviceInfo, so the bare name is ambiguous (CS0104).
        _toneMapEnabled = Microsoft.Maui.Devices.DeviceInfo.Current.Idiom != Microsoft.Maui.Devices.DeviceIdiom.TV;
        if (_toneMapEnabled)
        {
            try { player.SetVideoEffects(new List<AndroidX.Media3.Common.IEffect>()); }
            catch (System.Exception ex) { _toneMapEnabled = false; System.Diagnostics.Debug.WriteLine($"HDR tone-map (video effects) unavailable: {ex.Message}"); }
        }

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
        //
        // Use the PLURAL setPreferredAudioLanguages/Text with an explicit English fallback. The singular
        // setPreferredAudioLanguage falls back to ExoPlayer's DEFAULT track when the preferred language is
        // absent — which is whatever the container flags/orders first (a Greek-preferring user with no Greek
        // track was landing on Spanish). An ordered list [preferred, en] makes English the fallback before
        // ExoPlayer's default kicks in. Codes are normalised by ExoPlayer, so "en" matches eng/english.
        var audioLang = string.IsNullOrWhiteSpace(request.PreferredAudioLanguage) ? null : request.PreferredAudioLanguage;
        var textLang = string.IsNullOrWhiteSpace(request.PreferredSubtitleLanguage) ? "en" : request.PreferredSubtitleLanguage;
        var tsp = player.TrackSelectionParameters!.BuildUpon();
        if (audioLang is not null) tsp.SetPreferredAudioLanguages(WithEnglishFallback(audioLang));
        tsp.SetPreferredTextLanguages(WithEnglishFallback(textLang));
        player.TrackSelectionParameters = tsp.Build();

        // Resume via the start-position overload (no need to wait for a "ready" callback to seek).
        var startMs = request.ResumeSeconds is > 0 ? (long)(request.ResumeSeconds.Value * 1000) : 0L;
        player.SetMediaItem(item, startMs);
        player.Prepare();
        player.PlayWhenReady = true;

        _skipIntro = request.SkipIntro;
        _skipOutro = request.SkipOutro;
        _skipRecap = request.SkipRecap;
        if (_skipIntro is not null || _skipOutro is not null || _skipRecap is not null) EnsureSkipButton(platformView);

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
        _skipButton = null;   // child of platformView, torn down with it
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
                    UpdateSkipButton(pos / 1000.0);
                    if (_toneMapEnabled && !_lookApplied) MaybeApplyHdrLook(p);
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

    // Preferred-language list with English as an explicit fallback: [lang] when it's already English,
    // else [lang, "en"]. ExoPlayer tries them in order before its own default, so a missing preferred
    // language lands on English rather than an arbitrary container-default track.
    private static string[] WithEnglishFallback(string lang)
        => string.Equals(lang, "en", System.StringComparison.OrdinalIgnoreCase)
            ? new[] { "en" }
            : new[] { lang, "en" };

    // Once the decoder reports the video format, decide whether to push the HDR "look". For HDR streams
    // (PQ/ST2084 or HLG transfer) swap the empty tone-map pipeline for one that brightens/adds punch/boosts
    // saturation, so the tone-mapped SDR result matches libVLC's vivid output. SDR streams are left on the
    // (no-op) empty pipeline — they're already correctly graded. Runs once per playback either way.
    private void MaybeApplyHdrLook(IExoPlayer p)
    {
        var fmt = p.VideoFormat;
        if (fmt is null) return;          // wait until the decoder reports a format
        _lookApplied = true;              // only evaluate once, HDR or not

        // ColorInfo.ColorTransfer: 6 = ST2084/PQ, 7 = HLG (both HDR). Many containers/decoders don't surface
        // ColorInfo at all (transfer comes back unset), so an HDR stream would otherwise be left washed-out —
        // fall back to the release-metadata HDR flag (request.IsHdr) when the decoder doesn't report it.
        int transfer = fmt.ColorInfo?.ColorTransfer ?? -1;
        bool decoderHdr = transfer == 6 || transfer == 7;
        bool metadataHdr = VirtualView?.Request?.IsHdr ?? false;
        if (!decoderHdr && !metadataHdr) return;

        try
        {
            p.SetVideoEffects(new List<AndroidX.Media3.Common.IEffect>
            {
                new AndroidX.Media3.Effect.Brightness(ToneMapBrightness),
                new AndroidX.Media3.Effect.Contrast(ToneMapContrast),
                new AndroidX.Media3.Effect.HslAdjustment.Builder().AdjustSaturation(ToneMapSaturation).Build()!,
            });
        }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"HDR look effects unavailable: {ex.Message}"); }
    }

    // Build the "Skip Intro/Outro" overlay button once, anchored bottom-right over the PlayerView
    // (a FrameLayout). Focusable so the D-pad can land on it and OK skips. Hidden until in a band.
    private void EnsureSkipButton(PlayerView platformView)
    {
        if (_skipButton is not null) return;
        var btn = new Android.Widget.Button(Context!)
        {
            Text = "Skip Intro",
            Visibility = ViewStates.Gone,
            Focusable = true,
        };
        btn.SetAllCaps(false);
        btn.SetTextColor(Android.Graphics.Color.White);
        btn.SetBackgroundColor(Android.Graphics.Color.Argb(190, 0, 0, 0));
        btn.SetPadding(48, 24, 48, 24);
        var lp = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.End,
        };
        lp.SetMargins(0, 0, 56, 132);   // clear of PlayerView's bottom control bar
        btn.LayoutParameters = lp;
        btn.Click += (_, _) =>
        {
            try
            {
                var p = _player;
                if (p is not null)
                {
                    // An outro usually runs to the end, so End+1 lands at/after EOF. Clamp to ~1.5s before
                    // the end so the seek lands on real data and rolls into the natural end, rather than
                    // refusing the seek / snapping back.
                    var target = _skipTargetMs;
                    var dur = p.Duration;
                    if (dur > 0 && target > dur - 1500) target = dur - 1500;
                    if (target < 0) target = 0;
                    p.SeekTo(target);
                }
            }
            catch { /* not seekable yet */ }
            btn.Visibility = ViewStates.Gone;
        };
        platformView.AddView(btn);
        _skipButton = btn;
    }

    // Show / hide the skip button from the position ticker. Mirrors the libVLC player's ActiveBand.
    private void UpdateSkipButton(double t)
    {
        if (_skipButton is null) return;
        var band = ActiveSkipBand(t);
        if (band is null)
        {
            if (_skipButton.Visibility == ViewStates.Visible) _skipButton.Visibility = ViewStates.Gone;
            return;
        }
        _skipTargetMs = (long)((band.Value.End + 1) * 1000);   // land just past the band
        _skipButton.Text = band.Value.Label;
        if (_skipButton.Visibility != ViewStates.Visible)
        {
            _skipButton.Visibility = ViewStates.Visible;
            _skipButton.RequestFocus();    // TV: put the D-pad on it so OK skips immediately
        }
    }

    private (string Label, double End)? ActiveSkipBand(double t)
    {
        if (_skipIntro is { } i && t >= i.Start && t < i.End - 0.5) return ("Skip Intro", i.End);
        if (_skipRecap is { } r && t >= r.Start && t < r.End - 0.5) return ("Skip Recap", r.End);
        if (_skipOutro is { } o && t >= o.Start && t < o.End - 0.5) return ("Skip Outro", o.End);
        return null;
    }

    private void CycleScale()
    {
        if (PlatformView is null) return;
        _scaleIndex = (_scaleIndex + 1) % ResizeModes.Length;
        PlatformView.ResizeMode = ResizeModes[_scaleIndex];
        // The button is a single aspect-ratio icon (no text) — surface the new mode as a toast.
        Toast.MakeText(Context, ScaleLabels[_scaleIndex], ToastLength.Short)?.Show();
    }

    // Inject the on-screen aspect-ratio control into ExoPlayer's bottom control bar, next to the settings/CC
    // buttons. ExoPlayer's bar isn't publicly extensible, so we find the settings button's container and add an
    // aspect-ratio icon button that cycles Fit → Zoom → Fill on click (the same CycleScale the MENU key uses,
    // which toasts the new mode).
    private void EnsureAspectButton()
    {
        if (_aspectButton is not null || PlatformView is null) return;
        if (FindExoView("exo_settings")?.Parent is not ViewGroup bar) return;

        var ctx = PlatformView.Context!;
        var density = ctx.Resources?.DisplayMetrics?.Density ?? 1f;
        // An icon button (aspect-ratio glyph) rather than a "Fit/Zoom/Fill" text label, to match the
        // other bottom-bar controls. The active mode is announced via a toast on each tap (CycleScale).
        // Fully qualified: ImageButton/ImageView are ambiguous with the Microsoft.Maui.Controls types
        // pulled in by the project's implicit usings.
        var button = new Android.Widget.ImageButton(ctx)
        {
            Focusable = true,
            Clickable = true,
        };
        button.SetImageResource(Resource.Drawable.aspect_ratio);
        button.SetScaleType(Android.Widget.ImageView.ScaleType.FitCenter!);
        var pad = (int)(8 * density);
        button.SetPadding(pad, pad, pad, pad);
        button.Background = BuildFocusSelector(ctx);
        button.Click += (_, _) => CycleScale();

        // Place it just before the settings (gear) button so it reads with the other bottom-bar controls.
        var index = bar.IndexOfChild(FindExoView("exo_settings"));
        bar.AddView(button, index < 0 ? bar.ChildCount : index);
        // The bottom control bar is a horizontal LinearLayout; centre the button vertically so it lines
        // up with the CC / gear icons rather than floating to the top.
        if (button.LayoutParameters is LinearLayout.LayoutParams llp)
        {
            llp.Gravity = GravityFlags.CenterVertical;
            button.LayoutParameters = llp;
        }
        _aspectButton = button;
        _highlighted.Add(button); // already has the focus selector; keep the tree walk from re-applying it
    }

    // Resolve one of ExoPlayer's own control views by its resource name. The Media3 UI library's ids are merged
    // under the app package at build time, so look them up by name rather than depending on a generated constant.
    private Android.Views.View? FindExoView(string idName)
    {
        if (PlatformView is null) return null;
        var id = Context!.Resources!.GetIdentifier(idName, "id", Context.PackageName);
        return id == 0 ? null : PlatformView.FindViewById(id);
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

    // Media3 needs an explicit MIME type for sideloaded subtitle tracks and picks the parser from it — so it
    // must match what the URL actually serves. Default to SubRip (most OpenSubtitles downloads are .srt, and
    // the proxied URLs often carry no extension).
    private static string GuessSubtitleMime(string url)
    {
        var u = url.ToLowerInvariant();
        // Our subtitle proxy (/api/v1/subtitle?url=…) always converts upstream SRT/ASS to WebVTT, and the
        // positioning pass on that path emits VTT cue settings (line/position/align). Force text/vtt so
        // ExoPlayer uses the VTT parser — the SubRip parser would choke on the WEBVTT header and, even if it
        // didn't, drop the positioning, collapsing sign translations onto the dialogue. The encoded upstream
        // url= can still say ".srt", so this check must come before the extension sniffing below.
        if (u.Contains("/api/v1/subtitle")) return "text/vtt";
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
