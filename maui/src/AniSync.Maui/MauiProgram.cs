using System.Net.Http;
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

        // Which AniSync API the native app talks to. RELEASE ships against production;
        // DEBUG points at the locally-run web head (same machine) so the app exercises
        // your local server changes (rate-limit exemption, caching, …) WITHOUT a redeploy
        // — run the AniSync.Web project alongside this one. NOTE: the Android emulator
        // reaches the host via 10.0.2.2, not localhost; adjust there if you test Android.
#if DEBUG
        const string apiBaseUrl = "https://localhost:7131/";
#else
        const string apiBaseUrl = "https://anisync.fly.dev/";
#endif

        // ---- Shared client registrations (identical on both heads) ----
        builder.Services.AddScoped<AppState>();                 // session/nav/media-type/config state
        builder.Services.AddScoped<IAniSyncApi, AniSyncApi>();
        builder.Services.AddHttpClient<IAniSyncApi, AniSyncApi>((sp, http) =>
        {
            var env = sp.GetRequiredService<IAppEnvironment>();
            http.BaseAddress = new Uri(env.ApiBaseUrl);
        })
#if DEBUG
        // Trust the local dev HTTPS cert when talking to the local web head.
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
#endif
        ;

        // ---- MAUI head: native environment + secure storage (Keychain/KeyStore/DPAPI) ----
        builder.Services.AddSingleton<IAppEnvironment>(new MauiAppEnvironment(apiBaseUrl));
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
