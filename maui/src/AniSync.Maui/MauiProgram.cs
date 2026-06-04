using Microsoft.Extensions.Logging;
using AniSync.Client.Services;
using AniSync.Maui;
using LibVLCSharp.Shared;

namespace AniSync;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // ---- Shared client registrations (identical on both heads) ----
        builder.Services.AddScoped<AppState>();                 // session/nav/media-type/config state
        builder.Services.AddScoped<IAniSyncApi, AniSyncApi>();
        builder.Services.AddHttpClient<IAniSyncApi, AniSyncApi>((sp, http) =>
        {
            var env = sp.GetRequiredService<IAppEnvironment>();
            http.BaseAddress = new Uri(env.ApiBaseUrl);
        });

        // ---- MAUI head: native environment + secure storage (Keychain/KeyStore/DPAPI) ----
        builder.Services.AddSingleton<IAppEnvironment>(new MauiAppEnvironment("https://anisync.fly.dev/"));
        builder.Services.AddSingleton<ISecureStore, MauiSecureStore>();
        // Native sign-in: WebAuthenticator OAuth (anisync:// callback) + Kitsu password
        // grant, exchanged for the config segment. See MauiNativeAuth for the per-platform
        // URI-scheme wiring the callback needs.
        builder.Services.AddSingleton<INativeAuth, MauiNativeAuth>();

        // ---- LibVLCSharp: software-decodes HEVC/AC3/EAC3/DTS/TrueHD (the audio-codec fix) ----
        Core.Initialize();
        builder.Services.AddSingleton(_ => new LibVLC());
        builder.Services.AddSingleton<IMediaPlayer, VlcMediaPlayer>();

        return builder.Build();
    }
}
