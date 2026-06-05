using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace AniSync.Web;

/// <summary>
/// Restricts MVC controller discovery (over the referenced AniSync assembly) to
/// just the JSON API controllers the apps consume. The old app's UI controllers
/// (Home / Meta / Discover / Library) are attribute-routed at the same paths as the
/// Blazor pages, so admitting them would cause route collisions — this provider keeps
/// them out while exposing the API + the Stremio stream endpoint (used by the Watch
/// page). CalendarController is admitted (its colliding MVC /calendar view action was
/// dropped) for its single /api/v1/me/calendar JSON twin.
/// </summary>
public sealed class ApiOnlyControllerProvider : ControllerFeatureProvider
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "ApiController",            // /api/v1/*
        "ApiActorsController",      // /api/v1/actors, /api/v1/actors/tmdb/{id} (Discover → Actors + ActorDetail)
        "MetaProxyController",      // /api/v1/subtitle(.vtt), /api/v1/sub/{enc}/subtitle.vtt, /api/v1/resolve-stream (Watch proxies)
        "UserApiController",        // /api/v1/me/*
        "ConfigApiController",      // /api/v1/me/config, stream-addons, scrobble, export/import, danger zone
        "NotificationsController",  // /api/v1/notifications/*
        "CalendarController",       // /api/v1/me/calendar (MVC /calendar view action dropped to avoid the Blazor route collision)
        "CronController",           // /api/v1/cron/*
        "ScrobbleController",       // /api/v1/scrobble/{token}
        "PushController",           // /api/v1/push/*
        "StreamController",         // {config}/stream/{type}/{id}.json (Watch sources)
        // AuthController is the ported MVC login/link/logout flow (conventional
        // /Auth/{action} routes). Unlike the filtered-out Home/Meta/Discover UI
        // controllers it doesn't collide with a Blazor page route, and the Web head
        // needs it so the same provider-OAuth + Kitsu sign-in the web app uses works.
        "AuthController",           // /Auth/{action}
    };

    protected override bool IsController(TypeInfo typeInfo)
        => base.IsController(typeInfo) && Allowed.Contains(typeInfo.Name);
}
