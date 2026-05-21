using AnimeList.Services;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.OpenApi.Models;
using System.IO.Compression;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
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
builder.Services.AddControllersWithViews()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
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
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAllOrigins",
        builder =>
        {
            builder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
        });
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
// providers' rate limits. The Stremio addon endpoints aren't gated since
// Stremio retries are bounded and the user is the rate limit there.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
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
builder.Services.AddSingleton<IWatchingCacheStore, WatchingCacheStore>();
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

var app = builder.Build();

// Must run before any middleware that reads Request.Scheme / Request.IsHttps (HSTS,
// HttpsRedirection, the Razor view that builds the Stremio install URL, …).
app.UseForwardedHeaders();

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
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseCors("AllowAllOrigins");
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
