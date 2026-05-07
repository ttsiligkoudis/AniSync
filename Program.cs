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
builder.Services.AddSession();
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
            "discovery, recommendations, external streaming links, AniSkip OP/ED markers " +
            "and AnimeFillerList episode categorisation. User-scoped endpoints under " +
            "`/users/{config}` accept the UID as either the path segment or the " +
            "`X-AniSync-Config` header (preferred — keeps the UID out of URLs and access logs).\n\n" +
            "**Rate limit:** 60 requests / minute / IP, fixed-window. Bursts above the limit " +
            "receive a 429 with no queueing.",
    });
    options.DocInclusionPredicate((_, apiDesc) =>
        apiDesc.RelativePath?.StartsWith("api/v1") == true);
    // Stable, predictable operationIds (e.g. "getMapping", "saveEntry") so Swagger
    // Codegen / OpenAPI Generator produce client SDK methods named the way humans
    // would name them, not "apiV1MappingsIdGet".
    options.CustomOperationIds(api =>
        api.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor c
            ? char.ToLowerInvariant(c.ActionName[0]) + c.ActionName[1..]
            : null);
    // Document the alternative auth header on every user-scoped endpoint via a
    // global parameter so it surfaces in Swagger UI's "Try it out" panel.
    options.AddSecurityDefinition("ConfigHeader", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = "X-AniSync-Config",
        In = ParameterLocation.Header,
        Description = "Config UID, alternative to the {config} path segment. " +
                      "Preferred for HTTP API clients — keeps the UID out of URLs and access logs.",
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
});

builder.Services.AddSingleton<IAnimeMappingService, AnimeMappingService>();
builder.Services.AddSingleton<IConfigStore, SqliteConfigStore>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAnilistService, AnilistService>();
builder.Services.AddScoped<IKitsuService, KitsuService>();
builder.Services.AddScoped<IMalService, MalService>();
builder.Services.AddScoped<ITmdbService, TmdbService>();
builder.Services.AddScoped<ICinemetaService, CinemetaService>();
builder.Services.AddScoped<IAnilistFallback, AnilistFallback>();
builder.Services.AddScoped<ISyncService, SyncService>();
// Singleton — its (malId, episode) → markers cache is the whole point. Per-request
// scoping would defeat the cache. The service depends only on IHttpClientFactory
// which is itself singleton-safe.
builder.Services.AddSingleton<IAniSkipService, AniSkipService>();
// Same reasoning — the slug → episode-category cache should outlive any single
// request, and AnimeFillerList scrapes are expensive enough that we really want
// them cached for days, not seconds.
builder.Services.AddSingleton<IFillerListService, FillerListService>();

var app = builder.Build();

// Must run before any middleware that reads Request.Scheme / Request.IsHttps (HSTS,
// HttpsRedirection, the Razor view that builds the Stremio install URL, …).
app.UseForwardedHeaders();

// Pre-warm the anime ID mapping cache at startup
await app.Services.GetRequiredService<IAnimeMappingService>().EnsureLoadedAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseResponseCompression();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseCors("AllowAllOrigins");
app.UseSession();
app.UseAuthorization();

// Swagger UI lives at /api/docs so the addon's /Home/Index landing page isn't
// shadowed. The raw spec is at /api/swagger/v1/swagger.json.
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
