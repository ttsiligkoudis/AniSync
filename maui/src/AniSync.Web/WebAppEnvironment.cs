using AniSync.Client.Services;
using Microsoft.AspNetCore.Components;

namespace AniSync.Web;

/// <summary>
/// Web head's <see cref="IAppEnvironment"/>. The Blazor Web App hosts the JSON
/// API in the same process, so by default the thin client talks to its **own
/// origin** — resolved per-circuit from <see cref="NavigationManager.BaseUri"/>.
/// That matters for auth: an OAuth / Kitsu login writes the account row into
/// this host's SQLite store, so the <c>X-AniSync-Config</c> credential we seed
/// from it only resolves if the API calls hit the same host (not a remote Fly
/// origin). An explicit <c>ApiBaseUrl</c> config value overrides this (e.g. to
/// point a dev UI at a deployed backend). No native playback — the Watch page
/// renders an HTML5 &lt;video&gt; instead.
/// </summary>
public sealed class WebAppEnvironment : IAppEnvironment
{
    private readonly NavigationManager _nav;
    private readonly string? _override;
    private readonly AppState _state;

    public WebAppEnvironment(NavigationManager nav, IConfiguration config, AppState state)
    {
        _nav = nav;
        _override = config["ApiBaseUrl"];
        _state = state;
    }

    public string ApiBaseUrl => string.IsNullOrWhiteSpace(_override) ? _nav.BaseUri : _override;
    public bool IsNative => false;
    public bool SupportsNativePlayback => false;
    // Browser-on-TV can't be auto-detected server-side, but `?tv=1` opts into a full TV-shell
    // preview: MainLayout sets AppState.ForceTv from the query / persisted flag. Off by default.
    public bool IsTv => _state.ForceTv;
}
