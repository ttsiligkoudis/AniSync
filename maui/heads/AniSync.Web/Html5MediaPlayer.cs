using AniSync.Client.Services;

namespace AniSync.Web;

/// <summary>
/// Web head's <see cref="IMediaPlayer"/>. On the web the Watch page renders an
/// HTML5 &lt;video&gt; element directly (bound to the selected source URL), so
/// this implementation is a no-op — it exists only to satisfy the DI contract
/// the shared Watch page resolves. Browser codec limits apply here (the reason
/// the MAUI head uses LibVLCSharp instead).
/// </summary>
public sealed class Html5MediaPlayer : IMediaPlayer
{
    public Task PlayAsync(PlaybackRequest request, CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}
