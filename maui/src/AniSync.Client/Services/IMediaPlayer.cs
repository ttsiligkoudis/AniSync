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
    Action<string, string?>? OnStreamEvent = null);

public record SubtitleTrack(string Url, string Label, string? Language = null);
