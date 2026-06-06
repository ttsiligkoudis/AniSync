using AniSync.Client.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;

namespace AniSync.Web;

/// <summary>
/// Web head's <see cref="IMediaPlayer"/>. The shared Watch page renders the player
/// host (a &lt;div id="watch-video"&gt; container); this drives it through the
/// <c>anisyncWatch</c> JS module (wwwroot/js/watch-player.js): the module mounts the
/// ArtPlayer engine into that container (theme / ±10s controls / settings menu /
/// fullscreenWeb / the embedded-MKV subtitle pipeline), sets the source, seeks to
/// the resume position once metadata loads, and forwards <c>timeupdate</c> /
/// <c>ended</c> / stream-error events back to the page's callbacks so
/// resume-persistence, auto-track and auto-play-next behave exactly as they do on
/// the native LibVLCSharp head. Browser codec limits still apply here (the reason
/// the MAUI head uses libVLC).
/// </summary>
public sealed class Html5MediaPlayer : IMediaPlayer, IAsyncDisposable
{
    private const string VideoElementId = "watch-video";
    private const string ConfigHeader = "X-AniSync-Config";
    // ArtPlayer + watch-player.js are loaded lazily here (off every non-watch page) the
    // first time a source plays — see wwwroot/js/watch-loader.js.
    private const string ArtPlayerUrl = "https://unpkg.com/artplayer@5/dist/artplayer.js";
    private const string WatchPlayerUrl = "js/watch-player.js";

    private readonly IJSRuntime _js;
    private readonly AppState _state;
    private readonly IAppEnvironment _env;
    private readonly IConfiguration _config;
    private DotNetObjectReference<PlaybackCallbacks>? _ref;
    private IJSObjectReference? _loader;

    public Html5MediaPlayer(IJSRuntime js, AppState state, IAppEnvironment env, IConfiguration config)
    {
        _js = js;
        _state = state;
        _env = env;
        _config = config;
    }

    public async Task PlayAsync(PlaybackRequest request, CancellationToken ct = default)
    {
        DisposeRef();
        _ref = DotNetObjectReference.Create(
            new PlaybackCallbacks(request.OnProgress, request.OnEnded, request.OnStreamEvent));

        var options = new
        {
            url = request.Url,
            title = request.Title,
            resumeSeconds = request.ResumeSeconds ?? 0,
            subtitles = (request.Subtitles ?? Array.Empty<SubtitleTrack>())
                .Where(s => !string.IsNullOrEmpty(s.Url))
                .Select(s => new { url = s.Url, label = s.Label, lang = s.Language, language = s.Language })
                .ToArray(),
        };

        try
        {
            // Lazy-load the video stack on first play (ArtPlayer + watch-player.js are no
            // longer on every page). Resolves once window.anisyncWatch is defined, so the
            // anisyncWatch.* calls below are safe; watch-player.js's play() polls for
            // window.Artplayer itself, so the engine just needs to be on its way.
            _loader ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/watch-loader.js");
            await _loader.InvokeVoidAsync("ensure", ct, ArtPlayerUrl, WatchPlayerUrl);

            // Seed the environment half of the embedded-subtitle pipeline's context
            // (the API origin + config credential it fetches /episode-subtitles +
            // /resolve-stream with, plus the optional CF CORS proxy). The page seeds
            // the per-episode half (id / season / episode / type / title / skipTimes)
            // via its own setContext call; the JS merges both. The CORS proxy is
            // unset by default → embedded extraction skips CORS-blocked debrid hosts
            // silently, matching the original's CORS_PROXY_URL-unset behaviour.
            await _js.InvokeVoidAsync("anisyncWatch.setContext", ct, new
            {
                apiBase = _env.ApiBaseUrl,
                configHeader = ConfigHeader,
                config = _state.StreamConfig ?? string.Empty,
                clientHeader = AniSyncApi.ClientHeaderName,
                clientValue = AniSyncApi.ClientHeaderValue,
                corsProxyUrl = (_config["CORS_PROXY_URL"]
                    ?? Environment.GetEnvironmentVariable("CORS_PROXY_URL") ?? string.Empty).Trim(),
                corsProxySecret = (_config["CORS_PROXY_SECRET"]
                    ?? Environment.GetEnvironmentVariable("CORS_PROXY_SECRET") ?? string.Empty).Trim(),
            });
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_loader is not null)
        {
            try { await _loader.DisposeAsync(); } catch { }
            _loader = null;
        }
    }

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
