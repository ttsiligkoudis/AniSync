using AniSync.Client.Services;
using Microsoft.JSInterop;

namespace AniSync.Web;

/// <summary>
/// Web <see cref="ISecureStore"/>. Non-sensitive keys (media-type prefs, dashboard filter, …) are
/// localStorage via JS interop. The config credential (<see cref="ISecureStore.ConfigKey"/>) is the
/// exception: it is NEVER kept in localStorage — it's derived server-side from the HttpOnly
/// <c>anisync_uid</c> cookie (<see cref="IPrerenderSession.ResolveCredentialAsync"/>), so it can't be
/// lifted by XSS. Reads return the derived value; writes are no-ops because the cookie is authoritative
/// and only changes via server round-trips (login <c>/auth/complete</c>, logout <c>/Auth/Logout</c>,
/// regenerate <c>/auth/regenerate</c>), each of which rewrites the cookie on a real browser response.
/// Note: JS interop isn't available during prerender, so localStorage calls swallow that exception and
/// return null; the credential path doesn't touch JS, so it works during prerender too (cookie-based).
/// </summary>
public sealed class WebSecureStore : ISecureStore
{
    private readonly IJSRuntime _js;
    private readonly IPrerenderSession _session;

    public WebSecureStore(IJSRuntime js, IPrerenderSession session)
    {
        _js = js;
        _session = session;
    }

    public async Task<string?> GetAsync(string key)
    {
        if (key == ISecureStore.ConfigKey)
            return await _session.ResolveCredentialAsync();   // server-derived; never localStorage

        try { return await _js.InvokeAsync<string?>("localStorage.getItem", key); }
        catch { return null; }   // prerender / storage blocked
    }

    public async Task SetAsync(string key, string? value)
    {
        if (key == ISecureStore.ConfigKey) return;   // cookie is authoritative — nothing to persist

        try
        {
            if (string.IsNullOrEmpty(value))
                await _js.InvokeVoidAsync("localStorage.removeItem", key);
            else
                await _js.InvokeVoidAsync("localStorage.setItem", key, value);
        }
        catch { /* prerender / storage blocked */ }
    }
}
