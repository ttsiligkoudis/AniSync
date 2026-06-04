using AniSync.Client.Services;

namespace AniSync.Web;

/// <summary>
/// Web head's <see cref="IAppEnvironment"/>. Same-origin API (the Blazor Web
/// App is served alongside the backend, or proxies to it), and no native
/// playback — the Watch page renders an HTML5 &lt;video&gt; instead.
/// </summary>
public sealed class WebAppEnvironment : IAppEnvironment
{
    public string ApiBaseUrl { get; }
    public bool IsNative => false;
    public bool SupportsNativePlayback => false;

    public WebAppEnvironment(string apiBaseUrl = "/") => ApiBaseUrl = apiBaseUrl;
}
