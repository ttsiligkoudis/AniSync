using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace AniSync.Web;

/// <summary>
/// Restricts MVC controller discovery (over the referenced AniSync assembly) to
/// just the JSON API controllers the apps consume. The old app's UI controllers
/// (Home / Meta / Discover / Library) are attribute-routed at the same paths as the
/// Blazor pages, so admitting them would cause route collisions — this provider keeps
/// them out while exposing the JSON API plus the Stremio addon protocol endpoints
/// (manifest / catalog / subtitles / stream), whose {config}/... routes don't collide
/// with any Blazor page. CalendarController is admitted (its colliding MVC /calendar
/// view action was dropped) for its single /api/v1/me/calendar JSON twin.
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
        // Stremio addon protocol endpoints — all pure {config}/... routes with no UI-route
        // collisions, so they're safe to admit alongside the Blazor pages. Without these the
        // install URL ({config}/manifest.json) 404s and the addon can't register at all.
        "ManifestController",       // {config}/manifest.json (the install URL)
        "CatalogController",        // {config}/catalog/{type}/{list}.json (user-list + discover catalogs)
        "SubtitlesController",      // {config}/subtitles/{type}/{id}/{file}.json
        "StreamController",         // {config}/stream/{type}/{id}.json (Watch sources + addon streams)
        // NOTE: the addon's META resource ({config}/meta/{type}/{id}.json) lives in MetaController,
        // which is deliberately filtered out because its UI routes (/meta/{*id}, /meta/{id}/watch)
        // collide with the Blazor Detail/Watch pages. Catalogs built with "Group anime seasons" emit
        // IMDb ids, so Cinemeta serves their meta and the addon works without it; native-id
        // (grouping-off) catalogs would need a MetaController split to serve meta from here.
        // AuthController is the ported MVC login/link/logout flow (conventional
        // /Auth/{action} routes). Unlike the filtered-out Home/Meta/Discover UI
        // controllers it doesn't collide with a Blazor page route, and the Web head
        // needs it so the same provider-OAuth + Kitsu sign-in the web app uses works.
        "AuthController",           // /Auth/{action}
    };

    protected override bool IsController(TypeInfo typeInfo)
        => base.IsController(typeInfo) && Allowed.Contains(typeInfo.Name);
}
