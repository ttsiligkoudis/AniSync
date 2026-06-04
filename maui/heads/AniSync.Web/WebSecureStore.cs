using AniSync.Client.Services;
using Microsoft.JSInterop;

namespace AniSync.Web;

/// <summary>
/// Web <see cref="ISecureStore"/> backed by localStorage via JS interop. Note:
/// JS interop isn't available during server prerendering, so either disable
/// prerender for the app's render mode or hydrate after first interactive
/// render — the calls below swallow the prerender-time exception and return
/// null so startup hydration simply no-ops until interactivity.
/// </summary>
public sealed class WebSecureStore : ISecureStore
{
    private readonly IJSRuntime _js;
    public WebSecureStore(IJSRuntime js) => _js = js;

    public async Task<string?> GetAsync(string key)
    {
        try { return await _js.InvokeAsync<string?>("localStorage.getItem", key); }
        catch { return null; }   // prerender / storage blocked
    }

    public async Task SetAsync(string key, string? value)
    {
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
