using AniSync.Client.Services;
using LibVLCSharp.Shared;
// LibVLCSharp.Shared also defines a SubtitleTrack; alias the bare name to ours to avoid CS0104.
using SubtitleTrack = AniSync.Client.Services.SubtitleTrack;
// NOTE: the libVLC player type is referenced as the fully-qualified LibVLCSharp.Shared.MediaPlayer
// throughout this file. On iOS the SDK exposes a top-level `MediaPlayer` namespace (Apple's
// MediaPlayer.framework binding), so the bare name resolves to that namespace (CS0118) and a
// `using MediaPlayer = …` alias collides with it (CS0576) — qualifying is the only clean option.

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
    private readonly ISecureStore _store;
    private LibVLCSharp.Shared.MediaPlayer? _player;

    // Settings-store key for the chosen native engine (Account → Appearance → Video player).
    // "exo" → ExoPlayer; anything else (incl. unset) → the default libVLC. Android phones only;
    // TV ignores it (always ExoPlayer) and non-Android has no ExoPlayer to switch to.
    private const string PlayerPrefKey = "pref:player";

    public VlcMediaPlayer(LibVLC libVlc, ISecureStore store) => (_libVlc, _store) = (libVlc, store);

    public async Task PlayAsync(PlaybackRequest request, CancellationToken ct = default)
    {
        // Tear down any prior session before starting a new one.
        await StopAsync();

        // Read unconditionally (not inside #if ANDROID) so _store is used on every target —
        // avoids an "assigned but never used" warning on the Windows build, where the branch
        // below is compiled out.
        var prefersExo = string.Equals(await _store.GetAsync(PlayerPrefKey), "exo", StringComparison.OrdinalIgnoreCase);

#if ANDROID
        // Android TV: play with ExoPlayer (Google Media3, via our ExoVideoView handler), the engine
        // Stremio uses. The embedded libVLCSharp SurfaceView could never present a 4K frame on these TVs
        // (the MAUI wrapper's surface integration, not libVLC itself); ExoPlayer plays 4K cleanly. Phones
        // default to the in-app libVLC MAUI player below (full chrome / exotic audio codecs) but can opt
        // into ExoPlayer via the setting — so this branch also runs when the user picked it.
        if (DeviceInfo.Current.Idiom == DeviceIdiom.TV || prefersExo)
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var nav = Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation
                    ?? throw new InvalidOperationException("No navigation host available to present the player.");
                await nav.PushModalAsync(new ExoPlayerPage(request));
            });
            return;
        }
#endif

        try
        {
            using var media = new Media(_libVlc, new Uri(request.Url));

            // Resume position: libVLC takes a start time in seconds via :start-time.
            if (request.ResumeSeconds is > 0)
                media.AddOption($":start-time={(int)request.ResumeSeconds.Value}");

            // This embedded MAUI path now serves phones only (TV goes to ExoPlayerPage above). SOFTWARE
            // decode is right for phones: some mobile chipsets' HEVC/10-bit hardware decoders corrupt the
            // picture into green/blocky artifacts, and a phone CPU keeps up fine (verified on-screen — a
            // 3840x2160 stream displays ~24fps steadily with no dropped frames).
            var player = new LibVLCSharp.Shared.MediaPlayer(media) { EnableHardwareDecoding = false };
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
                // libVLC ignores WebVTT cue positioning (every cue lands at the bottom) but
                // renders ASS \an/\pos natively — so ask our proxy for the ASS variant, which
                // keeps OpenSubtitles sign/song positioning. Only our own proxy understands
                // fmt=ass; any other URL is left alone. (The TV/ExoPlayer head above keeps VTT.)
                .Select(s => s with { Url = PreferAssSubtitle(s.Url) })
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
                    request.PreferredAudioLanguage, request.PreferredSubtitleLanguage, request.Diagnostics,
                    request.SkipIntro, request.SkipOutro, request.SkipRecap);
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

    // Point a proxied subtitle URL (/api/v1/subtitle?url=…) at the ASS variant (fmt=ass) so
    // libVLC gets positioning it can render. Non-proxy URLs are returned unchanged — fmt=ass
    // is only meaningful to our own proxy.
    private static string PreferAssSubtitle(string url)
    {
        if (string.IsNullOrEmpty(url) || url.IndexOf("/api/v1/subtitle", StringComparison.OrdinalIgnoreCase) < 0)
            return url;
        return url + (url.Contains('?') ? "&" : "?") + "fmt=ass";
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
