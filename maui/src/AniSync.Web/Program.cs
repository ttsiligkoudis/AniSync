using System.IO.Compression;
using System.Threading.RateLimiting;
using AniSync.Web;
using AniSync.Web.Components;
using AniSync.Client.Services;
using AnimeList.Services;
using AnimeList.Services.Filters;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ===========================================================================
//  Blazor Web head (UI) + AniSync JSON API, hosted together in one process.
//
//  The API controllers + services live in the self-contained AniSync.Server
//  library (copied from the ASP.NET app; no dependency on it). This file is the
//  single host: it serves the Blazor UI AND the /api/v1 surface the apps call,
//  so the whole thing ships as one Fly deployment. The server bootstrap below
//  mirrors the original ASP.NET Program.cs; the cookie/CSRF-for-UI bits that
//  only the old MVC UI needed (global CsrfOrAjaxFilter, session rehydrate
//  middleware) are intentionally dropped — the API is header-authenticated and
//  Blazor brings its own antiforgery.
// ===========================================================================

// ---- Blazor (interactive server) ----
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---- Shared client registrations (identical on both heads) ----
builder.Services.AddScoped<AppState>();                 // session/nav/media-type/config state
builder.Services.AddScoped<IAniSyncApi, AniSyncApi>();
builder.Services.AddHttpClient<IAniSyncApi, AniSyncApi>((sp, http) =>
{
    var env = sp.GetRequiredService<IAppEnvironment>();
    http.BaseAddress = new Uri(env.ApiBaseUrl);
});

// ---- Web head: browser environment, HTML5 <video> player, localStorage secure store ----
// NOTE: this base URL is also what the server-side prerender HttpClient uses to
// reach the API. Now that this head hosts the API at the same origin you may
// point it at the local address in dev; left at the Fly origin for parity.
builder.Services.AddSingleton<IAppEnvironment>(new WebAppEnvironment("https://anisync.fly.dev/"));
builder.Services.AddScoped<IMediaPlayer, Html5MediaPlayer>();
builder.Services.AddScoped<ISecureStore, WebSecureStore>();

// ===========================================================================
//  Server / API services (mirrors the ASP.NET app's Program.cs).
// ===========================================================================

// Recycle pooled HttpClient connections every 5 min (avoids wedged-pool stalls
// on long-running stream-addon fetches). See the original Program.cs comment.
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpClientDefaults(b =>
    b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }));

// Persistent session cookie (30d) so the installed PWA "stays logged in".
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".AniSync.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.MaxAge = TimeSpan.FromDays(30);
    options.IdleTimeout = TimeSpan.FromDays(30);
});

// API controllers only — the ApiOnlyControllerProvider filters out the old
// MVC UI controllers so they can't collide with the Blazor pages. The
// controllers come from the AniSync.Server assembly (added as an app part).
builder.Services.AddControllers()
    .ConfigureApplicationPartManager(apm =>
    {
        // Replace (not augment) the default provider — otherwise it would still
        // discover the UI controllers and union them back in.
        var def = apm.FeatureProviders
            .OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider>()
            .FirstOrDefault();
        if (def is not null)
            apm.FeatureProviders.Remove(def);
        apm.FeatureProviders.Add(new ApiOnlyControllerProvider());
    })
    .AddApplicationPart(typeof(AnimeList.Controllers.ApiController).Assembly)
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddHttpContextAccessor();

// Brotli + Gzip on outgoing responses (catalog/meta JSON compresses ~70%).
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

// Honour Fly's X-Forwarded-Proto/-For so Request.Scheme reports the original https.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.ForwardLimit = 1;
});

// Open CORS only for the Stremio addon protocol routes (applied via
// [EnableCors("AddonCors")] on those controllers). /api/v1/me/* stays same-origin.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AddonCors", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddHsts(options =>
{
    options.Preload = true;
    options.IncludeSubDomains = true;
    options.MaxAge = TimeSpan.FromDays(365);
});

// OpenAPI / Swagger for /api/v1 (UI mounted at /api/docs below).
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
            "the Config UID via the `X-AniSync-Config` request header.\n\n" +
            "**Rate limit.** 60 requests / minute / IP, fixed-window.",
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

    options.DocumentFilter<TagDescriptionsFilter>();
    options.DocInclusionPredicate((_, apiDesc) =>
        apiDesc.RelativePath?.StartsWith("api/v1") == true);
    options.CustomOperationIds(api =>
        api.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor c
            ? char.ToLowerInvariant(c.ActionName[0]) + c.ActionName[1..]
            : null);
    options.AddSecurityDefinition("ConfigHeader", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = "X-AniSync-Config",
        In = ParameterLocation.Header,
        Description = "Config UID. Required on every `/api/v1/me/*` endpoint. Never travels in the URL.",
    });

    var xmlPath = Path.Combine(AppContext.BaseDirectory, "AniSync.Server.xml");
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
});

// Per-IP / per-token / per-UID rate limits — controllers reference these policy
// names via [EnableRateLimiting("api" | "scrobble" | "addon")], so they must exist.
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
    options.AddPolicy("scrobble", httpContext =>
    {
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

builder.Services.AddMemoryCache();

// ---- Domain services (verbatim from the ASP.NET app) ----
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
builder.Services.AddSingleton<IAddonStreamService, AddonStreamService>();
builder.Services.AddSingleton<ISubtitleService, OpenSubtitlesService>();
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddScoped<IMergedListService, MergedListService>();
builder.Services.AddSingleton<IAniSkipService, AniSkipService>();
builder.Services.AddSingleton<IFillerListService, FillerListService>();
builder.Services.AddSingleton<IUserListCache, UserListCache>();

// Episode-release notification stack.
builder.Services.AddSingleton<INotificationStore, NotificationStore>();
builder.Services.AddSingleton<IHiddenEntryStore, HiddenEntryStore>();
builder.Services.AddSingleton<IWatchingCacheStore, WatchingCacheStore>();
builder.Services.AddSingleton<IPushSubscriptionStore, PushSubscriptionStore>();
builder.Services.AddSingleton<IPushNotificationService, PushNotificationService>();
builder.Services.AddSingleton<IAnimeScheduleService, AnimeScheduleService>();
builder.Services.AddScoped<IWatchingCacheRefreshService, WatchingCacheRefreshService>();
builder.Services.AddScoped<IEpisodeNotificationDispatcher, EpisodeNotificationDispatcher>();
builder.Services.AddSingleton<EpisodeNotificationScheduler>();
builder.Services.AddSingleton<IEpisodeNotificationScheduler>(sp =>
    sp.GetRequiredService<EpisodeNotificationScheduler>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<EpisodeNotificationScheduler>());

builder.Services.AddScoped<ISeriesEpisodeNotificationDispatcher, SeriesEpisodeNotificationDispatcher>();
builder.Services.AddHostedService<SeriesEpisodeNotificationScheduler>();

var app = builder.Build();

// Pre-warm the anime ID mapping cache (best-effort, capped so a slow GitHub CDN
// can't keep us from binding the port).
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

// Must run before anything that reads Request.Scheme / Request.IsHttps.
app.UseForwardedHeaders();

// Baseline security headers on every response.
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Content-Security-Policy"] = "base-uri 'self'; object-src 'none'; form-action 'self'";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseResponseCompression();
app.UseHttpsRedirection();

app.MapStaticAssets();

app.UseRouting();
app.UseRateLimiter();
app.UseCors();                 // activates [EnableCors("AddonCors")] endpoints
app.UseSession();
app.UseAntiforgery();          // required by Blazor interactive components
app.UseAuthorization();

// Swagger UI at /api/docs; raw spec at /api/swagger/v1/swagger.json.
app.UseSwagger(c => c.RouteTemplate = "api/swagger/{documentName}/swagger.json");
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/api/swagger/v1/swagger.json", "AniSync API v1");
    c.RoutePrefix = "api/docs";
    c.HeadContent = "<meta name=\"referrer\" content=\"no-referrer\">";
});

// API endpoints (attribute-routed: /api/v1/*, {config}/stream/*).
app.MapControllers();

// Blazor UI.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(AniSync.Client.Routes).Assembly);

app.Run();
