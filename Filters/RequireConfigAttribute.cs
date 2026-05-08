using AnimeList.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AnimeList.Filters
{
    /// <summary>
    /// Short-circuits the request to 401 when the <c>X-AniSync-Config</c> header
    /// is missing. Successful requests stash the trimmed value in
    /// <see cref="HttpContext.Items"/> under <see cref="ItemKey"/> so the action
    /// can read it via the controller's <c>ResolvedConfig</c> accessor.
    ///
    /// The UID never travels through the URL — that's deliberate. See the
    /// docstring on <see cref="Controllers.UserApiController"/> for the
    /// security rationale.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class RequireConfigAttribute : ActionFilterAttribute
    {
        public const string HeaderName = "X-AniSync-Config";
        public const string ItemKey = "AniSyncConfig";

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var header = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(header))
            {
                context.Result = new UnauthorizedObjectResult(
                    new ApiError($"{HeaderName} header required"));
                return;
            }
            context.HttpContext.Items[ItemKey] = header.Trim();
            base.OnActionExecuting(context);
        }
    }
}
