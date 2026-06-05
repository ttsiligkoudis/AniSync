using AniSync.Client.Services;
using Microsoft.JSInterop;

namespace AniSync.Web;

/// <summary>
/// Web head's <see cref="IMediaPlayer"/>. The shared Watch page renders an HTML5
/// &lt;video id="watch-video"&gt; element; this drives it through the
/// <c>anisyncWatch</c> JS module (wwwroot/js/watch-player.js): it sets the source,
/// attaches external subtitle tracks, seeks to the resume position once metadata
/// loads, and forwards <c>timeupdate</c> / <c>ended</c> back to the page's
/// callbacks so resume-persistence, auto-track and auto-play-next behave exactly
/// as they do on the native LibVLCSharp head. Browser codec limits still apply
/// here (the reason the MAUI head uses libVLC).
/// </summary>
public sealed class Html5MediaPlayer : IMediaPlayer, IAsyncDisposable
{
    private const string VideoElementId = "watch-video";

    private readonly IJSRuntime _js;
    private DotNetObjectReference<PlaybackCallbacks>? _ref;

    public Html5MediaPlayer(IJSRuntime js) => _js = js;

    public async Task PlayAsync(PlaybackRequest request, CancellationToken ct = default)
    {
        DisposeRef();
        _ref = DotNetObjectReference.Create(
            new PlaybackCallbacks(request.OnProgress, request.OnEnded, request.OnStreamEvent));

        var options = new
        {
            url = request.Url,
            resumeSeconds = request.ResumeSeconds ?? 0,
            subtitles = (request.Subtitles ?? Array.Empty<SubtitleTrack>())
                .Where(s => !string.IsNullOrEmpty(s.Url))
                .Select(s => new { url = s.Url, label = s.Label, language = s.Language })
                .ToArray(),
        };

        try
        {
            await _js.InvokeVoidAsync("anisyncWatch.play", ct, VideoElementId, options, _ref);
        }
        catch (JSDisconnectedException) { /* circuit closed mid-navigation */ }
        catch (OperationCanceledException) { }
    }

    public async Task SeekAsync(double seconds)
    {
        try { await _js.InvokeVoidAsync("anisyncWatch.seek", seconds); }
        catch (JSDisconnectedException) { }
        catch (OperationCanceledException) { }
    }

    public async Task StopAsync()
    {
        try { await _js.InvokeVoidAsync("anisyncWatch.stop"); }
        catch (JSDisconnectedException) { }
        catch (OperationCanceledException) { }
        catch (Exception) { /* best-effort teardown */ }
        DisposeRef();
    }

    private void DisposeRef()
    {
        _ref?.Dispose();
        _ref = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>JS-invokable bridge forwarding player events to the page callbacks.</summary>
    public sealed class PlaybackCallbacks
    {
        private readonly Action<double, double>? _onProgress;
        private readonly Action? _onEnded;
        private readonly Action<string, string?>? _onStreamEvent;

        public PlaybackCallbacks(
            Action<double, double>? onProgress,
            Action? onEnded,
            Action<string, string?>? onStreamEvent)
        {
            _onProgress = onProgress;
            _onEnded = onEnded;
            _onStreamEvent = onStreamEvent;
        }

        [JSInvokable] public void OnProgress(double position, double duration) => _onProgress?.Invoke(position, duration);
        [JSInvokable] public void OnEnded() => _onEnded?.Invoke();
        [JSInvokable] public void OnStreamEvent(string kind, string? reason) => _onStreamEvent?.Invoke(kind, reason);
    }
}
