namespace AniSync.Client.Services;

/// <summary>
/// Lets the web head seed <see cref="AppState"/> from the server-visible session — the HttpOnly
/// <c>anisync_uid</c> cookie set on login by the ported AuthController — during prerender, and carry
/// that verdict to the interactive circuit. Without it the server can't know during prerender whether
/// the user is signed in (the credential lives in localStorage, unreadable server-side), so the chrome
/// prerenders signed-out and flashes when the client hydrates. The cookie is unreadable from the
/// interactive circuit (HttpOnly, and the circuit has no HttpContext), so the web impl bridges the
/// prerender verdict across via <see cref="Microsoft.AspNetCore.Components.PersistentComponentState"/>.
/// Native (MAUI) has no prerender or cookie, so it uses <see cref="NoOpPrerenderSession"/> and relies
/// on the normal localStorage hydration in MainLayout.
/// </summary>
public interface IPrerenderSession
{
    /// <summary>
    /// Call once from MainLayout.OnInitialized. <paramref name="isInteractive"/> is the component's
    /// RendererInfo.IsInteractive. Seeds <paramref name="state"/> as signed-in when the server can see
    /// a session, so the chrome renders signed-in from the first paint with no flash.
    /// </summary>
    void Bootstrap(AppState state, bool isInteractive);
}

/// <summary>No-op (native / fallback): the normal localStorage hydration drives session state.</summary>
public sealed class NoOpPrerenderSession : IPrerenderSession
{
    public void Bootstrap(AppState state, bool isInteractive) { }
}
