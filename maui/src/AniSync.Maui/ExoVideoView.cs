using AniSync.Client.Services;

namespace AniSync.Maui;

/// <summary>
/// A thin MAUI <see cref="View"/> whose Android handler owns a Media3 <c>ExoPlayer</c> + <c>PlayerView</c>.
/// This is the TV player: ExoPlayer plays 4K cleanly where the embedded libVLC SurfaceView never presented a
/// frame, and PlayerView's built-in, D-pad-friendly controls (play/seek plus a settings menu for audio and
/// subtitle tracks and playback speed) give us the chrome for free. External subtitle tracks, preferred
/// audio/subtitle languages, resume position, progress/ended callbacks and video scaling are all wired up in
/// <c>ExoVideoViewHandler</c>. There is no handler on non-Android platforms — the native TV player is
/// Android-only (phones use the in-app libVLC player).
/// </summary>
public sealed class ExoVideoView : View
{
    public ExoVideoView(PlaybackRequest request) => Request = request;

    /// <summary>The stream + metadata (subtitles, resume, preferred languages, scrobble hooks) to play.</summary>
    public PlaybackRequest Request { get; }

    // Page → handler. Pause when the app is backgrounded and resume when it returns, so audio doesn't keep
    // playing behind an inactive app (matching the libVLC player's behaviour).
    internal event Action? PauseForBackgroundRequested;
    internal event Action? ResumeFromBackgroundRequested;

    public void PauseForBackground() => PauseForBackgroundRequested?.Invoke();
    public void ResumeFromBackground() => ResumeFromBackgroundRequested?.Invoke();
}
