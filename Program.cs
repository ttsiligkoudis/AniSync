using AnimeList.Services;
using AnimeList.Services.Filters;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.OpenApi.Models;
using System.IO.Compression;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Force the IHttpClientFactory primary handler to recycle its pooled
// connections every 5 minutes. The .NET default is InfiniteTimeSpan,
// which is a well-known landmine for long-running services: a
// connection that quietly wedges (upstream TCP half-close that didn't
// propagate, CDN edge rotation, transient DNS / route change, partial
// HTTP/2 deadlock) sticks in the pool forever and every subsequent
// request to that host fails until the process is restarted. Five
// minutes is short enough that recovery happens automatically within
// a viewing session, long enough to keep most of the connection-reuse
// benefit. Stream-addon fetches (Torrentio, MediaFusion, etc.) hit
// many third-party hosts at varying availability so this is exactly
// the workload that exposes the pitfall — restarting the Fly machine
// after stream addons "stopped working" matches a wedged-pool
// recovery curve. See:
//   https://learn.microsoft.com/dotnet/architecture/microservices/implement-resilient-applications/use-httpclientfactory-to-implement-resilient-http-requests#issues-with-the-original-httpclient-class-available-in-net
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpClientDefaults(b =>
    b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }));
// Persistent session cookie. The default AddSession() emits a "session
// cookie" with no MaxAge/Expires, which Android (and any browser, but
// most noticeably the PWA running in standalone mode) discards when the
// process ends — reopening the installed app then has no cookie to
// present, so the user lands on the login screen again even though
// they signed in last night. MaxAge=30d + matching IdleTimeout=30d
// gives a "stays logged in" experience that lines up with what users
// expect from any other tracker app.
//   - IsEssential=true marks the cookie as required for the site to
//     function so any future consent middleware doesn't suppress it.
//   - HttpOnly stays true — the access tokens stored server-side
//     under this session shouldn't be reachable from JS.
//   - SameSite=Lax (default) is correct: cross-site embeds shouldn't
//     carry the session, but top-level navigations (OAuth callbacks
//     from AniList / MAL) need the cookie attached on return.
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".AniSync.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    // Secure + SameSite set explicitly rather than leaning on framework
    // defaults. Behind Fly's TLS-terminating proxy the app sees the inbound
    // request as HTTP until UseForwardedHeaders rewrites the scheme, so the
    // "auto-Secure-when-HTTPS" default is order-sensitive; pin Always in prod
    // (SameAsRequest locally so plain-http dev still gets a cookie). SameSite
    // stays Lax — the intended value — but explicit so a framework upgrade
    // can't silently change it. Lax still attaches on the OAuth callback because
    // that return is a top-level navigation, not a cross-site embed.
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest
        : Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
    options.Cookie.MaxAge = TimeSpan.FromDays(30);
    options.IdleTimeout = TimeSpan.FromDays(30);
});
// Stremio's video renderer throws when fields like `released` are present with a
// null value — it expects either a valid ISO date string or the field absent.
// Drop nulls globally so any optional Meta / Video field that's unset on the C#
// side simply doesn't appear in the JSON.
//
// JsonStringEnumConverter renders enums (AnimeService, ListType, …) as their
// names instead of integer ordinals. Mostly cosmetic for our API responses
// (we ToString() most enums explicitly anyway), but matters for Swagger: the
// schema generator picks up the converter and emits string-typed enum
// dropdowns in /api/docs instead of "Available values: 0, 1, 2".
builder.Services.AddControllersWithViews(options =>
    {
        // Global anti-CSRF: every unsafe-method endpoint must present either an
        // antiforgery token (HTML forms) or X-Requested-With: XMLHttpRequest
        // (same-origin AJAX). External-trust endpoints opt out via
        // [IgnoreAntiforgeryToken] — see CsrfOrAjaxFilter for the details.
        options.Filters.Add<CsrfOrAjaxFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
// IAntiforgery + its DI plumbing. The token name `__RequestVerificationToken` is
// the framework default; setting Cookie.* explicitly so a future framework change
// can't silently drop the Secure / SameSite hardening we rely on.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
    options.Cookie.Name = ".AniSync.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest
        : Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
});
builder.Services.AddHttpContextAccessor();

// Brotli + Gzip on outgoing responses. Catalog / meta JSON is the bulk of bandwidth
// and compresses ~70%. EnableForHttps is required because the addon is served over
// HTTPS through Fly's edge proxy. CompressionLevel.Fastest is the right tradeoff
// here — payloads aren't huge and CPU time costs more than bandwidth on a shared
// VM. Order in the pipeline below: must come before UseStaticFiles / endpoints.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/json", "image/svg+xml" });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// Fly.io terminates TLS at its edge proxy and forwards to the app over plain HTTP, so
// Request.Scheme would otherwise read "http" and we'd hand Stremio Web an http:// install
// URL that browsers refuse to fetch as mixed content. Honour X-Forwarded-Proto / -For so
// scheme detection (and every Request.Scheme-built URL in the app) returns the original
// "https". Fly's proxy IPs aren't enumerable, so clear the default loopback-only allowlist;
// this is safe in our deployment because Fly's proxy is the only thing in front of us.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    // Trust exactly one hop. With the known-proxy allowlist cleared (Fly's edge
    // IPs aren't enumerable) the middleware would otherwise walk an arbitrarily
    // long, fully client-supplied X-Forwarded-For chain; capping at 1 means only
    // the entry Fly's proxy appends is ever consumed for Request scheme/host. The
    // per-IP rate limiter does NOT rely on this — it keys off Fly-Client-IP, which
    // Fly's edge sets and overwrites (so it can't be spoofed). See AddRateLimiter.
    //
    // TODO: bump to 2 if Cloudflare ever sits in front of Fly. With CF + Fly the
    // chain becomes [client, CF, Fly] and a limit of 1 would peel off Fly's entry
    // and read Cloudflare's IP as the client, breaking Referrer-based per-IP logs
    // (the rate limiter would still be fine — it keys off Fly-Client-IP).
    options.ForwardLimit = 1;
});

// Open CORS is applied *only* to the Stremio addon protocol routes (manifest /
// catalog / meta / stream / subtitles) via [EnableCors("AddonCors")] on those
// controllers. Stremio fetches them cross-origin from app.strem.io, the desktop
// app, and assorted web players, so AllowAnyOrigin is genuinely required there.
// It is deliberately NOT applied globally any more: the /api/v1/me/* surface and
// the web-app endpoints are same-origin only, so a malicious site can't read or
// write a user's library cross-origin even if it has somehow learned their
// Config UID. The browser extension talks to /api/v1 from its MV3 background
// context under host_permissions, which is exempt from CORS, so it is unaffected.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AddonCors",
        policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
});

// HSTS preload eligibility: long max-age + includeSubDomains + the preload flag.
// UseHsts() (non-dev, in the pipeline below) is what actually emits the header.
builder.Services.AddHsts(options =>
{
    options.Preload = true;
    options.IncludeSubDomains = true;
    options.MaxAge = TimeSpan.FromDays(365);
});

// OpenAPI / Swagger for the /api/v1 surface. The doc-comments on ApiController
// surface as endpoint descriptions thanks to GenerateDocumentationFile in the
// .csproj. The Stremio addon endpoints are excluded from the swagger doc via
// DocInclusionPredicate — they're consumed by Stremio itself, and their
// nested-segment routes (e.g. "{config}/stream/{type}/{id}.json") confuse
// Swashbuckle's path generation.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AniSync API",
        Version = "v1",
        Description =
            "HTTP API for cross-service anime mapping, unified anime detail, search, " +
            "discovery, recommendations, external streaming links, AniSkip OP/ED markers, " +
            "AnimeFillerList episode categorisation, and full library / sync management.\n\n" +
            "**Authentication.** User-scoped endpoints live under `/api/v1/me/*` and require " +
            "the Config UID via the `X-AniSync-Config` request header. The UID never appears " +
            "in the URL — that's deliberate, so it can't leak through Referer headers, " +
            "reverse-proxy / CDN access logs, browser history, or shared screenshots. " +
            "Stremio's addon protocol still embeds the UID in the path on its own routes " +
            "(catalog / meta / stream / subtitles / manifest) because the addon protocol has " +
            "no other transport — those routes are not part of `/api/v1`.\n\n" +
            "**Rate limit.** 60 requests / minute / IP, fixed-window. Bursts above the limit " +
            "receive a 429 with no queueing.\n\n" +
            "**Versioning.** All endpoints live under `/api/v1`. Breaking changes ship behind " +
            "a new `/api/v2` prefix; `v1` remains supported until explicitly sunsetted via " +
            "the operation deprecation marker plus a 6-month notice.",
        Contact = new OpenApiContact
        {
            Name = "AniSync on GitHub",
            Url = new Uri("https://github.com/ttsiligkoudis/AniSync"),
        },
        License = new OpenApiLicense
        {
            Name = "MIT",
            Url = new Uri("https://github.com/ttsiligkoudis/AniSync/blob/master/LICENSE"),
        },
    });

    // Tag descriptions surface in Swagger UI as section subtitles.
    options.DocumentFilter<TagDescriptionsFilter>();

    options.DocInclusionPredicate((_, apiDesc) =>
        apiDesc.RelativePath?.StartsWith("api/v1") == true);

    // Stable, predictable operationIds so generated SDKs (Swagger Codegen /
    // OpenAPI Generator) produce method names like saveEntry() and getAnime()
    // instead of apiV1MappingsIdGet().
    options.CustomOperationIds(api =>
        api.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor c
            ? char.ToLowerInvariant(c.ActionName[0]) + c.ActionName[1..]
            : null);

    // Document the alternative auth header on every user-scoped endpoint via a
    // global parameter so it surfaces in Swagger UI's "Authorize" panel.
    options.AddSecurityDefinition("ConfigHeader", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = "X-AniSync-Config",
        In = ParameterLocation.Header,
        Description = "Config UID. Required on every `/api/v1/me/*` endpoint. " +
                      "Never travels in the URL.",
    });

    var xmlPath = Path.Combine(AppContext.BaseDirectory, "AniSync.xml");
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
});

// Per-IP fixed-window rate limit on /api/v1/*. Conservative defaults — enough
// for an interactive client, low enough that an abuser can't pin the upstream
// providers' rate limits. The Stremio addon endpoints get their own generous
// per-UID "addon" policy below (legitimate Stremio traffic is self-limiting, but
// the routes shouldn't be unmetered once a UID is known).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(RateLimitKeys.ClientIp(httpContext), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        }));
    // Webhook ingestion (/api/v1/scrobble/{token}). Partitioned by token, not IP — bingers
    // behind a NAT / VPN exit shouldn't share a budget. Higher cap and a small queue because
    // event delivery from Plex/Jellyfin can be bursty (catch-up sessions, server restarts
    // flushing buffered events).
    options.AddPolicy("scrobble", httpContext =>
    {
        // Token is the last path segment of /api/v1/scrobble/{token}.
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var slash = path.LastIndexOf('/');
        var key = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 240,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 60,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
    // Stremio addon protocol routes (manifest / catalog / meta / stream / subtitles),
    // applied via [EnableRateLimiting("addon")] on those controllers. Partitioned by
    // the {config} UID in the path, not by IP: legitimate Stremio traffic for one
    // install is self-limiting, but once a UID is known the routes are otherwise
    // unmetered, so a generous per-UID cap stops a leaked/known UID from being
    // hammered. Falls back to the client IP if a route ever lacks a {config} segment.
    options.AddPolicy("addon", httpContext =>
    {
        var key = httpContext.Request.RouteValues.TryGetValue("config", out var cfg)
                  && cfg is string s && !string.IsNullOrEmpty(s)
            ? s
            : RateLimitKeys.ClientIp(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
});

// In-memory cache used by ScrobbleController to dedupe webhook deliveries inside a 60s
// window. Lightweight — entries auto-expire and the cardinality is at most one per
// (uid, anime, season, episode) currently being scrobbled.
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<IAnimeMappingService, AnimeMappingService>();
builder.Services.AddSingleton<IConfigStore, SqliteConfigStore>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAnilistService, AnilistService>();
builder.Services.AddScoped<IKitsuService, KitsuService>();
builder.Services.AddScoped<IMalService, MalService>();
builder.Services.AddScoped<ITmdbService, TmdbService>();
builder.Services.AddScoped<ICinemetaService, CinemetaService>();
builder.Services.AddScoped<ITraktService, TraktService>();
builder.Services.AddScoped<IAnilistFallback, AnilistFallback>();
builder.Services.AddScoped<IAnimeMetaLoader, AnimeMetaLoader>();
// Singleton so the (manifestUrl-fingerprint, stremio-id) → streams cache
// outlives individual requests — addon responses are stable enough that a
// 10-min cache slashes the upstream call rate when a user paginates through
// episodes. One generic service handles every Stremio-compatible stream
// addon (Torrentio / MediaFusion / Comet / Jackettio / AIOStreams / …);
// per-addon config lives inside each manifest URL the user pastes on the
// Configure page.
builder.Services.AddSingleton<IAddonStreamService, AddonStreamService>();
// Singleton so the (imdb, season, episode) → tracks cache + VTT body
// cache outlive individual requests — anime episodes are watched
// repeatedly and the same /watch view re-fetches on every visit.
builder.Services.AddSingleton<ISubtitleService, OpenSubtitlesService>();
builder.Services.AddScoped<ISyncService, SyncService>();
// Cross-provider list merge — backs the /library grid + dashboard Continue
// Watching shelf. Scoped because it composes per-request scoped services
// (Anilist / Kitsu / Mal + the per-user config store lookups).
builder.Services.AddScoped<IMergedListService, MergedListService>();
// Singleton — its (malId, episode) → markers cache is the whole point. Per-request
// scoping would defeat the cache. The service depends only on IHttpClientFactory
// which is itself singleton-safe.
builder.Services.AddSingleton<IAniSkipService, AniSkipService>();
// Same reasoning — the slug → episode-category cache should outlive any single
// request, and AnimeFillerList scrapes are expensive enough that we really want
// them cached for days, not seconds.
builder.Services.AddSingleton<IFillerListService, FillerListService>();
// Per-user list cache used by the dashboard + library web-app pages. Singleton
// so the cache outlives individual requests — invalidation happens explicitly
// on every save/delete (controller-side) and every linked-secondary fan-out
// write (SyncService-side), with a 10-minute TTL backstop for cross-channel
// writes (Plex/Jellyfin scrobble webhooks, edits made on the provider's own
// website, etc.).
builder.Services.AddSingleton<IUserListCache, UserListCache>();

// Episode-release notification stack.
//   - Stores are singleton (raw SQLite, same lifetime as IConfigStore which
//     owns the schema).
//   - AnimeScheduleService holds today's airing snapshot in memory and is
//     the source of truth for the bell's "nextAiringAt" — singleton so the
//     snapshot is shared.
//   - The dispatcher is scoped (depends on the per-service IAnilistService
//     / IMalService / IKitsuService); EpisodeNotificationScheduler creates
//     fresh scopes per timer fire.
//   - EpisodeNotificationScheduler is the hosted service that owns the
//     daily refresh + per-episode Task.Delay timers.
builder.Services.AddSingleton<INotificationStore, NotificationStore>();
builder.Services.AddSingleton<IHiddenEntryStore, HiddenEntryStore>();
builder.Services.AddSingleton<IWatchingCacheStore, WatchingCacheStore>();
// Excludes Trakt "series" entries that the user already tracks as anime (Calendar + series notifications).
builder.Services.AddSingleton<ITrackedAnimeImdbResolver, TrackedAnimeImdbResolver>();
builder.Services.AddSingleton<IPushSubscriptionStore, PushSubscriptionStore>();
builder.Services.AddSingleton<IPushNotificationService, PushNotificationService>();
builder.Services.AddSingleton<IAnimeScheduleService, AnimeScheduleService>();
builder.Services.AddScoped<IWatchingCacheRefreshService, WatchingCacheRefreshService>();
builder.Services.AddScoped<IEpisodeNotificationDispatcher, EpisodeNotificationDispatcher>();
// Register the scheduler under all three of its identities so the
// hosting infrastructure runs it as a BackgroundService AND
// controllers can inject IEpisodeNotificationScheduler to trigger
// refreshes on demand (used by CronController for the Cloudflare
// Worker's per-minute wake pings).
builder.Services.AddSingleton<EpisodeNotificationScheduler>();
builder.Services.AddSingleton<IEpisodeNotificationScheduler>(sp =>
    sp.GetRequiredService<EpisodeNotificationScheduler>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<EpisodeNotificationScheduler>());

// Series-episode notifications (Trakt). No external trigger surface — the
// hourly BackgroundService is the only driver (per-user Trakt reads can't run
// off the per-minute cron ping), so a plain hosted-service registration suffices.
builder.Services.AddScoped<ISeriesEpisodeNotificationDispatcher, SeriesEpisodeNotificationDispatcher>();
builder.Services.AddHostedService<SeriesEpisodeNotificationScheduler>();

var app = builder.Build();

// Must run before any middleware that reads Request.Scheme / Request.IsHttps (HSTS,
// HttpsRedirection, the Razor view that builds the Stremio install URL, …).
app.UseForwardedHeaders();

// Baseline security headers on every response — set before next() so they ride
// even on static files and the re-executed error pages. Referrer-Policy keeps the
// Config UID, which can appear in /{uid}/configure URLs, from leaking to third
// parties through the Referer header when a user follows an external link. The CSP
// is intentionally narrow: it locks down <base> hijacking, plugin/object embedding,
// and form targets (none of which the app uses) without constraining script / style
// / img / connect sources, so the existing inline scripts and remote poster CDNs
// keep working untouched.
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Content-Security-Policy"] = "base-uri 'self'; object-src 'none'; form-action 'self'";
    await next();
});

// Pre-warm the anime ID mapping cache at startup. Wrapped in
// try/catch + timeout because the upstream is raw.githubusercontent.com
// and its availability / Fly-IP rate limiting is out of our control —
// we don't want a slow or unreachable GitHub to keep us from binding
// to port 8080 (Fly's load balancer gives up after 15s and the
// machine never enters service). Every caller (AnilistFallback,
// MalService, KitsuService, etc.) already awaits EnsureLoadedAsync()
// before use, so a missed pre-warm just means the first user
// request pays the download cost instead of the deploy.
try
{
    var mappings = app.Services.GetRequiredService<IAnimeMappingService>();
    await mappings.EnsureLoadedAsync().WaitAsync(TimeSpan.FromSeconds(8));
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex,
        "Mapping pre-warm failed at startup; will load lazily on first request");
}

if (!app.Environment.IsDevelopment())
{
    // Re-execute /error/500 on any uncaught exception so users see the
    // friendly ServerError view instead of a stack trace. Dev keeps the
    // default developer exception page so the trace is right there
    // during local work.
    app.UseExceptionHandler("/error/500");
    app.UseHsts();
}

// Re-execute /error/{code} for any unhandled 4xx/5xx with no body —
// covers bad URLs (no route match → 404) and bare StatusCode(...)
// returns. Runs in both dev and prod so the friendly page is what
// users see locally too. Re-execute (not redirect) keeps the
// original URL in the address bar.
app.UseStatusCodePagesWithReExecute("/error/{0}");

app.UseResponseCompression();
app.UseHttpsRedirection();
// Cache-Control on static assets. By default UseStaticFiles emits no max-age,
// so browsers revalidate every asset on every navigation (a 304 round-trip per
// file) — Lighthouse flags this as an inefficient cache policy and it's real
// latency on mobile. asp-append-version stamps css/js/scoped-bundle URLs with a
// ?v=<content-hash>, so those bytes are immutable for that URL and can be cached
// for a year; the hash changes whenever the file does, so a deploy is picked up
// without any stale-asset risk. sw.js and the manifest are deliberately excluded
// (no-cache) so a new service worker / manifest is seen on the next visit rather
// than pinned behind a long TTL — the SW update-flow depends on that freshness.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? string.Empty;
        if (path.EndsWith("/sw.js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
            return;
        }
        // Versioned (fingerprinted) assets → immutable for a year; everything
        // else (icons, logo, screenshots, jquery) → a month, which still clears
        // the efficient-cache bar without pinning unfingerprinted art for a year.
        ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Query.ContainsKey("v")
            ? "public, max-age=31536000, immutable"
            : "public, max-age=2592000";
    }
});
app.UseRouting();
app.UseRateLimiter();
// Parameterless: activates CORS for endpoints carrying [EnableCors("AddonCors")]
// metadata (the Stremio addon controllers) and leaves everything else same-origin.
app.UseCors();
app.UseSession();

// Rehydrate the Session "AccessToken" entry from the persistent UID cookie
// whenever the session is empty. Without this, the very first request after
// a redeploy (or after the in-memory session store evicted the entry) renders
// the layout and dashboard as "not logged in" — every other controller path
// hits ITokenService.GetAccessTokenAsync which already does this rehydration,
// but the dashboard intentionally skips that to avoid token-refresh IO. The
// helper is a no-op once the session is populated, so the steady-state cost
// is a single empty-string check per request.
app.Use(async (ctx, next) =>
{
    var store = ctx.RequestServices.GetService<IConfigStore>();
    if (store != null)
    {
        await TokenService.TryRehydrateSessionFromCookieAsync(ctx, store);
    }
    await next();
});

app.UseAuthorization();

// Swagger UI lives at /api/docs so the addon's dashboard at / and configure
// page at /configure aren't shadowed. The raw spec is at /api/swagger/v1/swagger.json.
//
// HeadContent injects a strict referrer policy so users clicking links from
// the Swagger page (e.g. typing a UID into "Try it out" then following a link
// in a response body) don't leak the URL — including any UID embedded in it —
// via the Referer header to third parties.
app.UseSwagger(c => c.RouteTemplate = "api/swagger/{documentName}/swagger.json");
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/api/swagger/v1/swagger.json", "AniSync API v1");
    c.RoutePrefix = "api/docs";
    c.HeadContent = "<meta name=\"referrer\" content=\"no-referrer\">";
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

static class RateLimitKeys
{
    /// <summary>
    /// Partition key for per-client rate limits. Prefers Fly's <c>Fly-Client-IP</c>
    /// header: Fly's edge proxy sets and overwrites it with the real peer address,
    /// so unlike <c>X-Forwarded-For</c> a client can't spoof it to mint unlimited
    /// rate-limit buckets. Falls back to the connection remote IP (local dev /
    /// non-Fly front edges) and finally a constant so the limiter always has a key.
    /// </summary>
    public static string ClientIp(HttpContext ctx)
    {
        var fly = ctx.Request.Headers["Fly-Client-IP"].ToString();
        if (!string.IsNullOrWhiteSpace(fly)) return fly.Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }
}
