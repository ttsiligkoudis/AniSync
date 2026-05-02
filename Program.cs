using AnimeList.Services;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSession();
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

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

builder.Services.AddSingleton<IAnimeMappingService, AnimeMappingService>();
builder.Services.AddSingleton<IConfigStore, SqliteConfigStore>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAnilistService, AnilistService>();
builder.Services.AddScoped<IKitsuService, KitsuService>();
builder.Services.AddScoped<ITmdbService, TmdbService>();
builder.Services.AddScoped<ICinemetaService, CinemetaService>();
builder.Services.AddScoped<IAnilistFallback, AnilistFallback>();

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

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors("AllowAllOrigins");
app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
