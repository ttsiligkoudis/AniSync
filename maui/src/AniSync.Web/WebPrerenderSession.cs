using System.Linq;
using AniSync.Client.Services;
using AniSync.Client.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

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
    private readonly AuthenticationStateProvider _auth;

    public WebPrerenderSession(IHttpContextAccessor http, PersistentComponentState persist, AuthenticationStateProvider auth)
    {
        _http = http;
        _persist = persist;
        _auth = auth;
    }

    /// <summary>
    /// Resolve the v5 credential from the signed-in uid without touching the client: on prerender from
    /// the request's anisync_uid cookie (HttpContext present); on the interactive circuit (no HttpContext)
    /// from AuthenticationStateProvider, which the AniSyncUid scheme populated at connection time. Returns
    /// null when there's no session or anything goes wrong, so MainLayout safely falls back to localStorage.
    /// </summary>
    public async Task<string?> ResolveCredentialAsync()
    {
        try
        {
            // Prerender / any real HTTP request: the HttpOnly cookie is right here.
            var uid = _http.HttpContext?.Request.Cookies[UidCookieName];

            // Interactive circuit: no HttpContext, but the connection was authenticated → read the claim.
            if (string.IsNullOrEmpty(uid))
            {
                var authState = await _auth.GetAuthenticationStateAsync();
                uid = authState.User.FindFirst(UidAuthenticationHandler.UidClaimType)?.Value;
            }

            if (string.IsNullOrEmpty(uid)) return null;
            var cred = AnimeList.Utils.EncodeV5Config(uid);
            return string.IsNullOrEmpty(cred) ? null : cred;
        }
        catch
        {
            return null; // never throw into MainLayout's startup path — fall back to localStorage
        }
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

            // Media-type prefs from cookies (written by media-type.js — same keys as the original web
            // app) so the toggle + the right active mode / dashboard filter render from the first paint,
            // instead of defaulting to anime and flashing once localStorage hydrates on the interactive
            // pass. Applies to anonymous visitors too (no credential needed).
            if (ctx is not null) SeedMediaTypes(state, ctx);
        }
        else
        {
            // Interactive: render the same signed-in chrome the prerender committed (no flash). The
            // real config + connected label are filled in by MainLayout's localStorage hydration.
            if (_persist.TryTakeFromJson<bool>(PersistKey, out var loggedIn) && loggedIn)
                state.HydrateSession(true, "");

            // Restore the media-type prefs the prerender bridged so the toggle + active mode + dashboard
            // filter are correct from the FIRST interactive render — otherwise they'd blink to the
            // default (anime / no toggle) until MainLayout's async localStorage read lands.
            if (_persist.TryTakeFromJson<MediaSeed>(MediaPersistKey, out var ms) && ms is not null)
                ApplyMediaSeed(state, ms.Enabled, ms.Active, ms.Dash);
        }
    }

    // Persisted across prerender→interactive (non-sensitive, unlike the credential) so the media-type
    // chrome doesn't blink on hydration.
    private const string MediaPersistKey = "anisync.mediaTypes";
    private sealed record MediaSeed(string? Enabled, string? Active, string? Dash);

    // Prerender: mirror MainLayout's interactive (localStorage) media-type hydration, but from the
    // cookies media-type.js writes — so a configured user's enabled set, active mode and dashboard
    // filter are known at first paint. Only acts when a pref cookie exists, so first-visit users keep
    // their normal flow (default set + the chooser). Cookie values are URL-encoded (the enabled set is
    // a comma list → %2C), so unescape on read. Bridges the raw values to the interactive circuit.
    private void SeedMediaTypes(AppState state, Microsoft.AspNetCore.Http.HttpContext ctx)
    {
        var enabled = Decode(ctx.Request.Cookies["anisync_media_types"]);
        var active = Decode(ctx.Request.Cookies["anisync_media_type"]);
        var dash = Decode(ctx.Request.Cookies["anisync_dash_filter"]);

        // Nothing stored → leave un-hydrated so the interactive pass / first-visit chooser decide.
        if (string.IsNullOrEmpty(enabled) && string.IsNullOrEmpty(active)) return;

        ApplyMediaSeed(state, enabled, active, dash);
        _persist.RegisterOnPersisting(() =>
        {
            _persist.PersistAsJson(MediaPersistKey, new MediaSeed(enabled, active, dash));
            return Task.CompletedTask;
        });
    }

    // Parse the (cookie- or persisted-) raw values into AppState. Shared by the prerender cookie read
    // and the interactive replay so both commit identical state.
    private static void ApplyMediaSeed(AppState state, string? enabledRaw, string? activeRaw, string? dashRaw)
    {
        var set = (enabledRaw ?? "")
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .Select(s => System.Enum.TryParse<MetaType>(s, out var mt) ? (MetaType?)mt : null)
            .Where(mt => mt.HasValue).Select(mt => mt!.Value).Distinct().ToList();

        if (set.Count > 0)
        {
            var active = System.Enum.TryParse<MetaType>(activeRaw, out var a) && set.Contains(a) ? a : set[0];
            state.SetEnabledMediaTypes(set, active);
        }
        else if (System.Enum.TryParse<MetaType>(activeRaw, out var a))
        {
            // Active set via the toggle but the enabled-set cookie isn't present → apply against the default set.
            state.SetMediaType(a);
        }

        if (dashRaw == "all"
            || (System.Enum.TryParse<MetaType>(dashRaw, out var dm) && (set.Count == 0 || set.Contains(dm))))
            state.SetDashboardFilter(dashRaw!);

        state.MarkMediaTypesHydrated();
    }

    private static string? Decode(string? raw)
        => string.IsNullOrEmpty(raw) ? raw : System.Uri.UnescapeDataString(raw);

    // Interactive: hand back whatever the prerender stashed under this key (and clear it, so a later
    // client-side navigation to the same page reloads normally with its skeleton).
    public bool TryReplay<T>(string key, out T value)
    {
        // Best-effort: a deserialise hiccup (shape drift, partial blob) must never throw into a page's
        // load path — the page just finds nothing to replay and loads normally (with its skeleton).
        try
        {
            var ok = _persist.TryTakeFromJson<T>(key, out var stored);
            value = stored!;
            return ok;
        }
        catch
        {
            value = default!;
            return false;
        }
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
