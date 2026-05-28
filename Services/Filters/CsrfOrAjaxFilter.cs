using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AnimeList.Services.Filters
{
    /// <summary>
    /// Global anti-CSRF filter. Same-origin POSTs were previously safe only by SameSite=Lax
    /// on the session cookie; a cookie-attribute regression would leave the state-changing
    /// endpoints exposed. This filter blocks every cross-site state change up front.
    ///
    /// Validation order, first match wins:
    ///   1. Safe HTTP method (GET / HEAD / OPTIONS / TRACE) — nothing to protect.
    ///   2. Endpoint carries [IgnoreAntiforgeryToken] — webhooks (scrobble) and external
    ///      JSON APIs (UserApiController / ApiController) opt out because they aren't
    ///      cookie-authenticated; they have their own bearer / URL-token / header auth.
    ///   3. <c>X-Requested-With: XMLHttpRequest</c> header is present — proof the request
    ///      came from same-origin JS, since browsers refuse to attach custom request
    ///      headers cross-origin without a CORS preflight and none of the in-app endpoints
    ///      enable permissive CORS (only the Stremio addon routes do, and those are GET).
    ///   4. Otherwise: validate the ASP.NET Core antiforgery token (HTML form posts).
    /// </summary>
    public class CsrfOrAjaxFilter : IAsyncAuthorizationFilter
    {
        private readonly IAntiforgery _antiforgery;

        public CsrfOrAjaxFilter(IAntiforgery antiforgery)
        {
            _antiforgery = antiforgery;
        }

        private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            HttpMethods.Get, HttpMethods.Head, HttpMethods.Options, HttpMethods.Trace,
        };

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var method = context.HttpContext.Request.Method;
            if (SafeMethods.Contains(method)) return;

            var endpoint = context.HttpContext.GetEndpoint();
            if (endpoint?.Metadata.GetMetadata<IgnoreAntiforgeryTokenAttribute>() != null) return;

            // Same-origin AJAX bypass. The browser SOP + our restrictive CORS means a
            // cross-origin attacker can't attach this header on a state-changing request
            // without a successful preflight, which our routes refuse. Equivalent to the
            // antiforgery token for CSRF purposes, much cheaper to set client-side.
            if (string.Equals(
                    context.HttpContext.Request.Headers["X-Requested-With"].ToString(),
                    "XMLHttpRequest",
                    StringComparison.Ordinal))
                return;

            try
            {
                await _antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                context.Result = new BadRequestObjectResult("Invalid antiforgery token.");
            }
        }
    }
}
