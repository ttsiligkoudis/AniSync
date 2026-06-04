using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace AniSync.Web;

/// <summary>
/// Restricts MVC controller discovery (over the referenced AniSync assembly) to
/// just the JSON API controllers the apps consume. The old app's UI controllers
/// (Home / Meta / Discover / Library / Calendar) are attribute-routed at the same
/// paths as the Blazor pages, so admitting them would cause route collisions —
/// this provider keeps them out while exposing the API + the Stremio stream
/// endpoint (used by the Watch page). Nothing in the old app is modified.
/// </summary>
public sealed class ApiOnlyControllerProvider : ControllerFeatureProvider
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "ApiController",            // /api/v1/*
        "UserApiController",        // /api/v1/me/*
        "NotificationsController",  // /api/v1/notifications/*
        "CronController",           // /api/v1/cron/*
        "ScrobbleController",       // /api/v1/scrobble/{token}
        "PushController",           // /api/v1/push/*
        "StreamController",         // {config}/stream/{type}/{id}.json (Watch sources)
    };

    protected override bool IsController(TypeInfo typeInfo)
        => base.IsController(typeInfo) && Allowed.Contains(typeInfo.Name);
}
