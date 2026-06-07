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

    /// <summary>
    /// Web only: on the interactive pass, replay a snapshot a page stashed during prerender so it can
    /// re-render its prerendered content instead of refetching — which otherwise flashes
    /// content → skeleton → content (and double-hits the API). Returns false on native, or when nothing
    /// was persisted under <paramref name="key"/> (e.g. a client-side navigation, which has no prerender).
    /// </summary>
    bool TryReplay<T>(string key, out T value);

    /// <summary>
    /// Web only: on the prerender pass, stash a snapshot under <paramref name="key"/> so the interactive
    /// remount can replay it via <see cref="TryReplay{T}"/>. No-op on native and when
    /// <paramref name="isInteractive"/> is true. <paramref name="snapshot"/> is invoked when state is
    /// persisted (end of prerender), so it captures the component's final loaded state.
    /// </summary>
    void Persist<T>(string key, bool isInteractive, Func<T> snapshot);

    /// <summary>
    /// Web only: resolve the X-AniSync-Config credential from the server-side session identity — the
    /// authenticated <c>anisync_uid</c> (from the request cookie during prerender, or from
    /// AuthenticationStateProvider on the interactive circuit). Returns the v5 credential so the web head
    /// authenticates API calls without the credential ever living in localStorage (removing the credential
    /// skeleton and keeping it out of XSS reach). This is the source WebSecureStore returns for the config
    /// key. Returns null on native and when no session can be resolved, so it's always safe to call.
    /// </summary>
    Task<string?> ResolveCredentialAsync();
}

/// <summary>No-op (native / fallback): the secure-store hydration in MainLayout drives session state.</summary>
public sealed class NoOpPrerenderSession : IPrerenderSession
{
    public void Bootstrap(AppState state, bool isInteractive) { }
    public bool TryReplay<T>(string key, out T value) { value = default!; return false; }
    public void Persist<T>(string key, bool isInteractive, Func<T> snapshot) { }
    public Task<string?> ResolveCredentialAsync() => Task.FromResult<string?>(null);
}
