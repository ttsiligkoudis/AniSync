namespace AniSync.Client.Services;

/// <summary>
/// Playback seam. The whole reason for going native: the MAUI head implements
/// this with LibVLCSharp (software-decodes HEVC / AC3 / EAC3 / DTS / TrueHD,
/// fixing the "video plays but no sound" problem the browser can't solve). The
/// Web head implements it with the existing HTML5 / ArtPlayer path via JS
/// interop, accepting the browser's codec limits.
/// </summary>
public interface IMediaPlayer
{
    /// <summary>
    /// Begin playback of a resolved stream URL (a debrid direct link).
    /// <paramref name="options"/> carries title, subtitle tracks, resume
    /// position and the scrobble hooks the watch page wires up.
    /// </summary>
    Task PlayAsync(PlaybackRequest request, CancellationToken ct = default);

    /// <summary>Seek the current playback to an absolute position in seconds.
    /// Used by the watch page's AniSkip "Skip intro/outro" action and auto-skip.</summary>
    Task SeekAsync(double seconds);

    /// <summary>Stop + tear down the current playback session.</summary>
    Task StopAsync();
}

/// <param name="Url">Resolved (playable) stream URL.</param>
/// <param name="Title">Display title shown in the player chrome.</param>
/// <param name="ResumeSeconds">Where to resume from, if any.</param>
/// <param name="Subtitles">Optional external subtitle tracks (url + label).</param>
/// <param name="OnProgress">Position callback (positionSeconds, durationSeconds)
///   raised periodically by the host player. The Watch page uses it to persist
///   resume position and scrobble — keeping that logic in the shared layer so
///   both the native (LibVLCSharp) and web (HTML5) hosts stay dumb.</param>
/// <param name="OnEnded">Raised when playback reaches the end (drives auto-play-next).</param>
/// <param name="OnStreamEvent">Web-head-only stream lifecycle signal driving the
///   fallback panel (port of Watch.cshtml's video:error / watchdog / stall wiring).
///   <c>kind</c> is "error" | "timeout" | "playing" | "stall"; <c>reason</c> (for
///   "error") is "network" | "decode". The native LibVLCSharp head decodes
///   everything and never raises this — its player has no fallback panel.</param>
public record PlaybackRequest(
    string Url,
    string Title,
    double? ResumeSeconds = null,
    IReadOnlyList<SubtitleTrack>? Subtitles = null,
    Action<double, double>? OnProgress = null,
    Action? OnEnded = null,
    Action<string, string?>? OnStreamEvent = null,
    // Preferred default languages (ISO 639-1). Audio is honoured by the native player only
    // (the browser doesn't expose track switching); subtitle applies to both heads. Null = English.
    string? PreferredAudioLanguage = null,
    string? PreferredSubtitleLanguage = null,
    // TEMP: free-form diagnostic string surfaced in the native player's subtitle sheet to debug the
    // "no subs on native" report (raw fetch count + request params the page used). Not shown on web.
    string? Diagnostics = null,
    // AniSkip OP/ED bands (absolute seconds) for the native players' "Skip Intro/Outro" button. The web
    // head drives its own skip overlay from JS, so these are only consumed by the native player pages.
    SkipMark? SkipIntro = null,
    SkipMark? SkipOutro = null);

/// <summary>An AniSkip band: skip from <paramref name="Start"/> to <paramref name="End"/> (absolute seconds).</summary>
public record SkipMark(double Start, double End);

public record SubtitleTrack(string Url, string Label, string? Language = null);
