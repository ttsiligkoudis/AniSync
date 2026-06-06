using AniSync.Client.Services;
using Microsoft.AspNetCore.Components;

namespace AniSync.Web;

/// <summary>
/// Web-head <see cref="IPrerenderSession"/>. During prerender it reads the <c>anisync_uid</c> cookie
/// (server-visible, HttpOnly, set on login by the ported AuthController and cleared by /Auth/Logout)
/// and, when present, marks the session signed-in so the chrome prerenders signed-in — eliminating the
/// "Sign in flashes then changes back" jank. It persists that verdict via
/// <see cref="PersistentComponentState"/> so the interactive circuit — which has no HttpContext and
/// can't read the HttpOnly cookie — renders the same signed-in chrome and never resets to neutral.
/// </summary>
public sealed class WebPrerenderSession : IPrerenderSession
{
    // Matches AniSync.Server TokenService.UidCookieName. Kept as a literal so this head doesn't take
    // a compile dependency on the server's internals just for one stable cookie name.
    private const string UidCookieName = "anisync_uid";
    private const string PersistKey = "anisync.session.loggedIn";

    private readonly IHttpContextAccessor _http;
    private readonly PersistentComponentState _persist;

    public WebPrerenderSession(IHttpContextAccessor http, PersistentComponentState persist)
    {
        _http = http;
        _persist = persist;
    }

    public void Bootstrap(AppState state, bool isInteractive)
    {
        if (!isInteractive)
        {
            // Prerender: the request carries the HttpOnly anisync_uid cookie when signed in.
            var ctx = _http.HttpContext;
            var uid = ctx?.Request.Cookies[UidCookieName];
            if (!string.IsNullOrEmpty(uid))
            {
                // Carry the signed-in verdict to the interactive circuit. We persist only when signed-in: a
                // missing cookie stays "unknown" so the interactive localStorage read can still find a
                // credential (e.g. a session created before this cookie existed) without a wrong commit.
                _persist.RegisterOnPersisting(() =>
                {
                    _persist.PersistAsJson(PersistKey, true);
                    return Task.CompletedTask;
                });
                state.HydrateSession(true, "");

                // Seed the actual config credential from the cookie's UID so prerender-time API calls
                // authenticate (X-AniSync-Config) and pages can render real user content on first paint
                // instead of a skeleton. A v5 credential is just [0x05][uid]; the server resolves it to the
                // user's DB row by UID — identical to the header the interactive client sends from
                // localStorage. This is server-side ONLY: we deliberately do NOT persist it into the page
                // (the UID stays HttpOnly; the interactive circuit re-reads the real credential from
                // localStorage via MainLayout). We also leave ConfigHydrated untouched so the dashboard /
                // chrome keep their existing prerender behaviour — only pages that read StreamConfig
                // directly (Library, Account) opt into prerendered content.
                try
                {
                    var cred = AnimeList.Utils.EncodeV5Config(uid);
                    if (!string.IsNullOrEmpty(cred))
                        state.SetStreamConfig(cred);
                }
                catch { /* malformed cookie → leave config unseeded; pages fall back to their skeleton */ }
            }
        }
        else if (_persist.TryTakeFromJson<bool>(PersistKey, out var loggedIn) && loggedIn)
        {
            // Interactive: render the same signed-in chrome the prerender committed (no flash). The
            // real config + connected label are filled in by MainLayout's localStorage hydration.
            state.HydrateSession(true, "");
        }
    }

    // Interactive: hand back whatever the prerender stashed under this key (and clear it, so a later
    // client-side navigation to the same page reloads normally with its skeleton).
    public bool TryReplay<T>(string key, out T value)
    {
        var ok = _persist.TryTakeFromJson<T>(key, out var stored);
        value = stored!;
        return ok;
    }

    // Prerender only: register a persist callback that serialises the page's snapshot into the response
    // so the interactive remount can replay it. Skipped on the interactive pass (nothing to bridge into).
    public void Persist<T>(string key, bool isInteractive, Func<T> snapshot)
    {
        if (isInteractive) return;
        _persist.RegisterOnPersisting(() =>
        {
            // Best-effort: a snapshot/serialise hiccup must not fail the prerender — the interactive
            // pass simply finds nothing to replay and reloads normally (with its skeleton).
            try { _persist.PersistAsJson(key, snapshot()); }
            catch { /* ignored */ }
            return Task.CompletedTask;
        });
    }
}
