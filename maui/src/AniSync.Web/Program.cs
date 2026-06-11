using System.IO.Compression;
using System.Threading.RateLimiting;
using AniSync.Web;
using AniSync.Web.Components;
using AniSync.Client.Services;
using AnimeList.Models;
using AnimeList.Services;
using AnimeList.Services.Extensions;
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

// Authenticate the HttpOnly anisync_uid cookie into HttpContext.User (presence-only; see
// UidAuthenticationHandler). This is what lets the interactive circuit learn the signed-in uid via
// AuthenticationStateProvider — so the web head derives the X-AniSync-Config credential server-side
// instead of round-tripping localStorage. Additive: no endpoint requires authorization, so existing
// (header-authenticated) API + addon routes are unaffected.
builder.Services.AddAuthentication(o =>
    {
        o.DefaultAuthenticateScheme = UidAuthenticationHandler.SchemeName;
        o.DefaultChallengeScheme = UidAuthenticationHandler.SchemeName;
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, UidAuthenticationHandler>(
        UidAuthenticationHandler.SchemeName, null);
// Surfaces AuthenticationState as a cascading parameter and backs the server AuthenticationStateProvider
// the circuit reads at connection time (no <CascadingAuthenticationState> wrapper needed).
builder.Services.AddCascadingAuthenticationState();

// ---- Shared client registrations (identical on both heads) ----
builder.Services.AddScoped<AppState>();                 // session/nav/media-type/config state
builder.Services.AddHttpContextAccessor();              // idempotent; WebPrerenderSession reads the anisync_uid cookie
// Cookie-backed prerender: render signed-in chrome from the first byte when the anisync_uid cookie is
// present, bridging the verdict to the interactive circuit via PersistentComponentState (no flash).
builder.Services.AddScoped<IPrerenderSession, WebPrerenderSession>();
builder.Services.AddScoped<IAniSyncApi, AniSyncApi>();
// AniSyncApi sets HttpClient.BaseAddress from IAppEnvironment in its constructor
// (the circuit scope), so the factory leaves it unset — the Web head's base URL is
// the per-request origin (scoped), which can't be read from the root provider the
// factory delegate would run under.
builder.Services.AddHttpClient<IAniSyncApi, AniSyncApi>()
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        // This head calls its OWN https origin server-side (the API is in-process). In
        // Development trust the loopback dev cert so logged-in /api/v1/me/* self-calls
        // don't fail when the ASP.NET dev cert isn't installed in the machine trust store.
        if (builder.Environment.IsDevelopment())
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return handler;
    });

// ---- Web head: browser environment, HTML5 <video> player, localStorage secure store ----
// IAppEnvironment is scoped so it can resolve the same-origin API base URL from
// NavigationManager per circuit: this head hosts the API + SQLite store, and an
// OAuth/Kitsu login writes the account row HERE, so the seeded X-AniSync-Config
// credential only resolves when /api/v1/me/* calls hit this same origin. Set the
// "ApiBaseUrl" config value to override (e.g. point a dev UI at a deployed backend).
builder.Services.AddScoped<IAppEnvironment, WebAppEnvironment>();
builder.Services.AddScoped<IMediaPlayer, Html5MediaPlayer>();
builder.Services.AddScoped<ISecureStore, WebSecureStore>();
// No OS system bars to tint on the web — the browser/PWA handles its own chrome (theme-color meta).
builder.Services.AddScoped<IPlatformChrome, NoOpPlatformChrome>();
// Web sign-in is the server redirect flow — the Login page uses /Auth/* anchors.
builder.Services.AddScoped<INativeAuth, NoopNativeAuth>();

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

// Session is backed by IDistributedCache (DistributedSessionStore). Register the
// in-memory implementation so the copied HomeController's HttpContext.Session
// works without an external cache — required by AddSession() below, and the DI
// container validates it on build in Development.
builder.Services.AddDistributedMemoryCache();

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
    {
        // Never rate-limit our OWN apps. The web head's server-side self-calls AND the
        // MAUI native apps (Windows / Android / iOS) all send the AniSync client header
        // (set on the shared typed HttpClient). Loopback is also exempt for local dev.
        // The 60/min cap is for third-party API consumers only — applying it to our
        // apps' dashboard fan-out emptied the page.
        if (httpContext.Request.Headers[AniSyncApi.ClientHeaderName].ToString() == AniSyncApi.ClientHeaderValue
            || (httpContext.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip)))
            return RateLimitPartition.GetNoLimiter("internal");
        return RateLimitPartition.GetFixedWindowLimiter(RateLimitKeys.ClientIp(httpContext), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
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
// One-time codes bridging native (MAUI) OAuth logins back to the app via anisync://.
builder.Services.AddSingleton<INativeAuthCodeStore, NativeAuthCodeStore>();

// Episode-release notification stack.
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
app.UseAuthentication();       // populates HttpContext.User from the anisync_uid cookie (AniSyncUid scheme)
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

// AuthController is the ported MVC controller (conventional /Auth/{action} routes,
// no [ApiController]/[Route]) that drives the OAuth + Kitsu login/link/logout flows
// against the session. Attribute-routed API controllers above are unaffected.
app.MapControllerRoute("auth", "Auth/{action}", new { controller = "Auth" });

// Debrid provider + catalog-addon list for the configure page's one-click stream
// setup (public — no per-user data). Mirrors what _StreamsSection.cshtml rendered
// server-side from StreamAddonCatalog.
app.MapGet("/api/v1/stream-catalog", () => Results.Json(new
{
    providers = AnimeList.Services.StreamAddonCatalog.Providers
        .Select(p => new { id = p.Id, name = p.DisplayName, apiKeyUrl = p.ApiKeyUrl, signUpUrl = p.SignUpUrl }),
    addons = AnimeList.Services.StreamAddonCatalog.Addons
        .Select(a => new { id = a.Id, name = a.DisplayName }),
}));

// Which login providers this host can actually start. Kitsu uses a username/
// password grant (no app registration), so it's always available; the OAuth
// providers are only offered when their ClientId is configured — the login UI
// hides the rest rather than dead-ending users on a "not configured" error.
app.MapGet("/api/v1/auth/providers", (IConfiguration cfg) => Results.Json(new
{
    kitsu = true,
    anilist = !string.IsNullOrWhiteSpace(cfg["Anilist:ClientId"]),
    mal = !string.IsNullOrWhiteSpace(cfg["Mal:ClientId"]),
    trakt = !string.IsNullOrWhiteSpace(cfg["Trakt:ClientId"]),
}));

// Native (MAUI) auth — one-time-code exchange + Kitsu password grant. The native app
// runs OAuth in its system browser against /Auth/Login?...&native=1, which redirects to
// anisync://auth?code=… on success; the app then exchanges the code here for its config
// segment. Kitsu (password grant) skips the browser entirely and posts straight here.
app.MapPost("/api/v1/auth/native/exchange", (NativeExchangeBody body, INativeAuthCodeStore codes) =>
{
    var uid = codes.Redeem(body?.Code);
    return string.IsNullOrEmpty(uid)
        ? Results.NotFound(new { error = "invalid or expired code" })
        : Results.Json(new { config = AnimeList.Utils.EncodeV5Config(uid) });
});

app.MapPost("/api/v1/auth/native/kitsu", async (NativeKitsuBody body, ITokenService tokenService, IConfigStore configStore) =>
{
    if (string.IsNullOrWhiteSpace(body?.Username) || string.IsNullOrWhiteSpace(body?.Password))
        return Results.BadRequest(new { error = "username and password required" });
    var token = await tokenService.GetAccessTokenByCredsAsync(body.Username, body.Password, setContext: false);
    if (token is null || string.IsNullOrEmpty(token.access_token))
        return Results.Json(new { error = "Kitsu credentials were rejected" }, statusCode: StatusCodes.Status401Unauthorized);
    var uid = await configStore.UpsertAsync(token);
    return Results.Json(new { config = AnimeList.Utils.EncodeV5Config(uid) });
});

// Auth → client config-seeding bridge. After a server-side OAuth/Kitsu login the
// session identifies the account; resolve its UID, encode the v5 config segment
// (the X-AniSync-Config credential the thin client sends), and hand it to the
// Blazor client via localStorage before navigating into the app. No resolvable
// session (e.g. just after logout) clears the stored credential instead. This runs
// as a normal HTTP request, so the session + anisync_uid cookie are present —
// unlike Blazor's server-side HttpClient calls, which can't see browser cookies.
app.MapGet("/auth/complete", async (HttpContext ctx, ITokenService tokenService, IConfigStore configStore) =>
{
    var returnUrl = ctx.Request.Query["returnUrl"].ToString();
    if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//"))
        returnUrl = "/";

    var (token, uid) = await tokenService.ResolveCurrentAsync(configStore);
    var segment = token is { anonymousUser: false } && !string.IsNullOrEmpty(uid)
        ? AnimeList.Utils.EncodeV5Config(uid)
        : null;

    // Phase 2: the credential is NEVER written to localStorage — the web circuit derives it
    // server-side from the HttpOnly anisync_uid cookie (WebSecureStore → ResolveCredentialAsync),
    // so XSS can't lift it. This bridge now only shows the themed screen and lands in the app; the
    // login/logout cookie was already set by AuthController / Auth/Logout before we got here. We also
    // best-effort scrub any legacy 'anisync.config' a pre-Phase-2 client may still have stored, and drop
    // the 24 h AniList stats cache so a fresh login / different user doesn't see the previous one's stats.
    var op = "localStorage.removeItem('anisync.config');localStorage.removeItem('anisync.stats.anilist');";
    var nav = System.Text.Json.JsonSerializer.Serialize(returnUrl);

    // Dark, themed loading screen instead of the browser's blank white page — this
    // bridge renders for a beat before location.replace() lands in the app, and a white
    // flash between two dark pages reads as a glitch. Spinner + a login/logout-aware line.
    var msg = string.IsNullOrEmpty(segment) ? "Signing you out…" : "Signing you in…";
    var html =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + msg + "</title>" +
        "<meta name=\"robots\" content=\"noindex\">" +
        "<style>html,body{margin:0;height:100%;background:#0b0d12;color:#e8e8ea;" +
        "font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif}" +
        ".auth-wrap{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:1.1rem}" +
        ".auth-spin{width:38px;height:38px;border-radius:50%;border:3px solid rgba(255,255,255,.14);" +
        "border-top-color:#4f7cff;animation:auth-spin .8s linear infinite}" +
        "@keyframes auth-spin{to{transform:rotate(360deg)}}" +
        ".auth-msg{font-size:.95rem;color:#9aa0ab;margin:0}</style></head>" +
        "<body><div class=\"auth-wrap\"><div class=\"auth-spin\"></div><p class=\"auth-msg\">" + msg + "</p></div>" +
        $"<script>try{{{op}}}catch(e){{}}location.replace({nav});</script>" +
        $"<noscript>Done. <a href=\"{System.Net.WebUtility.HtmlEncode(returnUrl)}\">Continue</a>.</noscript></body></html>";
    return Results.Content(html, "text/html");
});

// Danger-zone uid mutations. These run as same-origin POST fetches from the real browser page (not the
// circuit's loopback HttpClient, which can't carry a rewritten/cleared anisync_uid cookie back to the
// browser) so Set-Cookie actually reaches it; the client reloads afterward and the circuit re-derives
// the credential from the (new/absent) cookie. Credential secrecy is unaffected — the uid stays in the
// HttpOnly cookie, never the page. CSRF: POST (so a GET <img>/link can't trigger them) + the same
// X-Requested-With same-origin proof CsrfOrAjaxFilter uses (a cross-origin page can't set that header
// without a CORS preflight these routes refuse, and SameSite=Lax wouldn't send the cookie cross-site
// anyway). Minimal-API endpoints don't run the MVC CsrfOrAjaxFilter, so the check is inline here.
static bool IsSameOriginAjax(HttpContext ctx) =>
    string.Equals(ctx.Request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.Ordinal);

app.MapPost("/auth/regenerate", async (HttpContext ctx, ITokenService tokenService, IConfigStore configStore) =>
{
    if (!IsSameOriginAjax(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (_, uid) = await tokenService.ResolveCurrentAsync(configStore);
    if (!string.IsNullOrEmpty(uid))
    {
        var newUid = await configStore.RotateUidAsync(uid);
        if (!string.IsNullOrEmpty(newUid))
            tokenService.SetPrimaryUidCookie(newUid); // session AccessToken carries no uid; only the cookie needs rewriting
    }
    return Results.NoContent();
});

// Rotate the uid (kills every existing URL/credential everywhere) then sign THIS browser out:
// RemoveCachedUser resolves the token from the session blob (not the rotated uid), so it still clears
// the in-memory cache + session + uid cookie.
app.MapPost("/auth/signout-everywhere", async (HttpContext ctx, ITokenService tokenService, IConfigStore configStore) =>
{
    if (!IsSameOriginAjax(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (_, uid) = await tokenService.ResolveCurrentAsync(configStore);
    if (!string.IsNullOrEmpty(uid)) await configStore.RotateUidAsync(uid);
    await tokenService.RemoveCachedUser();
    return Results.NoContent();
});

// Delete the config row then sign this browser out. RemoveCachedUser runs first (the session still holds
// the token blob, so it resolves and clears session + cookie) before the row is gone.
app.MapPost("/auth/delete", async (HttpContext ctx, ITokenService tokenService, IConfigStore configStore) =>
{
    if (!IsSameOriginAjax(ctx)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var (_, uid) = await tokenService.ResolveCurrentAsync(configStore);
    await tokenService.RemoveCachedUser();
    if (!string.IsNullOrEmpty(uid)) await configStore.DeleteAsync(uid);
    return Results.NoContent();
});

// Blazor UI.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(AniSync.Client.Routes).Assembly);

app.Run();

// Request bodies for the native-auth minimal APIs (bound case-insensitively from JSON).
record NativeExchangeBody(string Code);
record NativeKitsuBody(string Username, string Password);
