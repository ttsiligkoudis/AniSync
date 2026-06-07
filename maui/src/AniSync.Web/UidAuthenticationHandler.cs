using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AniSync.Web;

/// <summary>
/// Authenticates a request from the HttpOnly <c>anisync_uid</c> cookie (set on login by the ported
/// AuthController). Presence of the cookie ⇒ an authenticated principal carrying the uid as a claim;
/// this is what lets the interactive Blazor circuit know the signed-in user WITHOUT the credential ever
/// living in the page or localStorage — the circuit reads the uid via <c>AuthenticationStateProvider</c>
/// (fed from this principal at connection time) and derives the X-AniSync-Config credential server-side.
///
/// Authentication here is deliberately presence-only (no DB round-trip per request): a forged uid still
/// yields a credential that resolves to nothing when the API validates it (ResolveConfigAsync), exactly
/// as today. It does NOT authorize anything on its own — no endpoint gains or loses access from this; it
/// only populates HttpContext.User so Blazor can surface the identity.
/// </summary>
public sealed class UidAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "AniSyncUid";

    /// <summary>Claim type the circuit reads to derive the credential. Also mapped to NameIdentifier.</summary>
    public const string UidClaimType = "anisync:uid";

    private const string UidCookieName = "anisync_uid"; // matches TokenService.UidCookieName

    public UidAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var uid = Request.Cookies[UidCookieName];
        if (string.IsNullOrEmpty(uid))
            return Task.FromResult(AuthenticateResult.NoResult()); // anonymous — not a failure

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(UidClaimType, uid),
                new Claim(ClaimTypes.NameIdentifier, uid),
            },
            SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
