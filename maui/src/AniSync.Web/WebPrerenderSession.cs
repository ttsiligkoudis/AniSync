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
            var loggedIn = ctx is not null && !string.IsNullOrEmpty(ctx.Request.Cookies[UidCookieName]);
            if (loggedIn)
            {
                // Carry the verdict to the interactive circuit. We persist only when signed-in: a
                // missing cookie stays "unknown" so the interactive localStorage read can still find a
                // credential (e.g. a session created before this cookie existed) without a wrong commit.
                _persist.RegisterOnPersisting(() =>
                {
                    _persist.PersistAsJson(PersistKey, true);
                    return Task.CompletedTask;
                });
                state.HydrateSession(true, "");
            }
        }
        else if (_persist.TryTakeFromJson<bool>(PersistKey, out var loggedIn) && loggedIn)
        {
            // Interactive: render the same signed-in chrome the prerender committed (no flash). The
            // real config + connected label are filled in by MainLayout's localStorage hydration.
            state.HydrateSession(true, "");
        }
    }
}
