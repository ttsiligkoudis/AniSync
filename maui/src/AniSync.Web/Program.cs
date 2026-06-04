using AniSync.Web.Components;
using AniSync.Client.Services;
using AniSync.Web;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
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
builder.Services.AddSingleton<IAppEnvironment>(new WebAppEnvironment("https://anisync.fly.dev/"));
builder.Services.AddScoped<IMediaPlayer, Html5MediaPlayer>();
builder.Services.AddScoped<ISecureStore, WebSecureStore>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(
        typeof(AniSync.Client.Routes).Assembly);

app.Run();
