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
public record PlaybackRequest(
    string Url,
    string Title,
    double? ResumeSeconds = null,
    IReadOnlyList<SubtitleTrack>? Subtitles = null,
    Action<double, double>? OnProgress = null,
    Action? OnEnded = null);

public record SubtitleTrack(string Url, string Label, string? Language = null);
