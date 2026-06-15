using AniSync.Client.Services;
using LibVLCSharp.Shared;
// LibVLCSharp.Shared also defines a SubtitleTrack; alias the bare name to ours to avoid CS0104.
using SubtitleTrack = AniSync.Client.Services.SubtitleTrack;

namespace AniSync.Maui;

/// <summary>
/// LibVLCSharp implementation of <see cref="IMediaPlayer"/> for the MAUI head —
/// the whole point of going native. libVLC software-decodes the codecs browsers
/// choke on (HEVC video, AC3 / EAC3 / DTS / TrueHD audio), so episodes that
/// "play with no sound" in a WebView play correctly here, with external subtitle
/// tracks and resume.
///
/// A native video surface can't live inside the BlazorWebView DOM, so playback
/// happens on a separate MAUI ContentPage (<see cref="VlcPlayerPage"/>) pushed
/// modally over the hybrid shell. The Watch page calls PlayAsync; this service
/// owns the LibVLC lifetime and hands the MediaPlayer to that page.
///
/// Packages (add to AniSync.Maui.csproj):
///   LibVLCSharp, LibVLCSharp.MAUI,
///   VideoLAN.LibVLC.Android (net9.0-android),
///   VideoLAN.LibVLC.Windows (net9.0-windows).
/// Call Core.Initialize() once in MauiProgram before registering this service.
/// </summary>
public sealed class VlcMediaPlayer : IMediaPlayer, IDisposable
{
    private readonly LibVLC _libVlc;
    private MediaPlayer? _player;

    public VlcMediaPlayer(LibVLC libVlc) => _libVlc = libVlc;

    public async Task PlayAsync(PlaybackRequest request, CancellationToken ct = default)
    {
        // Tear down any prior session before starting a new one.
        await StopAsync();

#if ANDROID
        // External VLC handoff is kept available but OFF. Testing showed Stremio plays this 4K stream with
        // its EMBEDDED VLC (its menu lists Exo/VLC/MPV as embedded players, with "External" as a separate
        // option), so embedded is the correct path — and ours stalled only because we start playback before
        // the video surface exists (fixed below + in the page). Flip this to delegate to the standalone VLC
        // app instead (shows the Android "Open with" picker).
        var useExternalVlcOnTv = false;
        if (useExternalVlcOnTv && DeviceInfo.Current.Idiom == DeviceIdiom.TV
            && await MainThread.InvokeOnMainThreadAsync(() => ExternalVlc.TryLaunch(request)))
        {
            return;
        }
#endif

        try
        {
            var isTv = DeviceInfo.Current.Idiom == DeviceIdiom.TV;

            using var media = new Media(_libVlc, new Uri(request.Url));

            // Resume position: libVLC takes a start time in seconds via :start-time.
            if (request.ResumeSeconds is > 0)
                media.AddOption($":start-time={(int)request.ResumeSeconds.Value}");

            if (isTv)
            {
                // TV 4K via embedded MediaCodec. Two on-screen-diagnosed failures had to be fixed together:
                //  • With direct rendering ON, MediaCodec's output buffers are owned by the surface and never
                //    released (nothing consumes them), so the decoder deadlocks after ~1 frame and demux
                //    chokes — total freeze at t=0.
                //  • With :no-mediacodec-dr (frames copied OUT of the codec) the pipeline runs, but the
                //    DEFAULT (GL) video output can't stand up a 4K surface ("vout x0" / shown 0) on these
                //    budget TV GPUs.
                // So: copy frames out here, and render them through the android-display vout
                // (--vout=android-display, set on the LibVLC instance for TV in MauiProgram) — it blits
                // straight to the ANativeWindow with no GL texture-size ceiling, the output VLC-Android
                // itself uses for 4K.
                media.AddOption(":no-mediacodec-dr");
            }

            // Decoding strategy is device-dependent:
            //  • Phones/tablets: SOFTWARE decode — some mobile chipsets' HEVC/10-bit hardware decoders corrupt
            //    the picture into green/blocky artifacts, and a phone CPU keeps up fine (verified on-screen: a
            //    3840x2160 stream displays ~24fps steadily with no dropped frames).
            //  • TV: HARDWARE MediaCodec decode — a weak TV CPU/GPU can't software-render 4K. Paired with the
            //    :no-mediacodec-dr + android-display vout combo above so the hardware-decoded 4K frames
            //    actually reach the screen (this is the path Stremio's embedded VLC uses to play 4K here).
            var player = new MediaPlayer(media) { EnableHardwareDecoding = isTv };
            _player = player;

            // External subtitle tracks (proxied OpenSubtitles URLs) are NOT pre-attached here: libVLC loads
            // slaves lazily and unreliably, so most never appeared in the track list (the player showed one
            // "SRT" while the web app listed English / Arabic / …). Hand the full list to the player page,
            // which shows every available language up-front and attaches the one the user picks on demand
            // (AddSlave select:true). Keep only well-formed absolute http(s) URLs — a relative or non-http
            // slave makes libVLC fall through to the SMB access module ("smb2_parse_url failed").
            var externalSubs = (request.Subtitles ?? Array.Empty<SubtitleTrack>())
                .Where(s => Uri.TryCreate(s.Url, UriKind.Absolute, out var u)
                            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                .ToList();

            // Report progress + completion back to the Watch page (it owns resume
            // persistence + scrobble). libVLC raises these on its own thread; the
            // page's handlers marshal back to the renderer (InvokeAsync / NavigateTo).
            // Capture the player instance (not the _player field) and swallow faults:
            // a callback that fires after Stop/Dispose, or a renderer that's been torn
            // down, would otherwise throw on libVLC's native thread and crash the app.
            if (request.OnProgress is not null)
            {
                player.TimeChanged += (_, e) =>
                {
                    try { request.OnProgress(e.Time / 1000.0, player.Length / 1000.0); }
                    catch { /* player stopped / renderer gone */ }
                };
            }
            if (request.OnEnded is not null)
            {
                player.EndReached += (_, _) => { try { request.OnEnded(); } catch { /* renderer gone */ } };
            }

            // A libVLC playback error (expired/blocked debrid link, undecodable stream) should tear the
            // session down cleanly rather than leave audio running behind a dead page.
            player.EncounteredError += (_, _) => _ = StopAsync();

            // Hand the configured player to a native page on the UI thread. The page itself calls Play()
            // once its VideoView's surface is actually created (see VlcPlayerPage) — NOT here right after
            // presenting. Starting before the surface exists is what left hardware MediaCodec with no output
            // surface on TV (decoded a few frames then stalled, video black), and starting before the page is
            // presented would leave audio playing with no visible surface ("frozen screen + background sound").
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var nav = Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation
                    ?? throw new InvalidOperationException("No navigation host available to present the player.");
                var page = new VlcPlayerPage(player, request.Title, externalSubs,
                    request.PreferredAudioLanguage, request.PreferredSubtitleLanguage);
                await nav.PushModalAsync(page);
            });
        }
        catch
        {
            // Failed to start playback — release the partially-built player so we don't leak it or leave a
            // stuck modal, then rethrow so the Watch page can show its fallback instead of the app crashing.
            await StopAsync();
            throw;
        }
    }

    public Task SeekAsync(double seconds)
    {
        if (_player is null) return Task.CompletedTask;
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            try { _player.Time = (long)(seconds * 1000); } catch { /* not seekable yet */ }
        });
    }

    public Task StopAsync()
    {
        if (_player is null) return Task.CompletedTask;
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            try { _player.Stop(); } catch { /* already stopped */ }
            _player.Dispose();
            _player = null;
        });
    }

    public void Dispose()
    {
        _player?.Dispose();
        _libVlc.Dispose();
    }
}
