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

#if ANDROID
        // Make the Android WebView transparent the moment its handler creates it. Chromium paints the
        // document white before the page's CSS background renders, which flashes white on cold start /
        // resume (most visible in dark mode). Transparent lets the dark window/content background show
        // through until the page paints. Done via the handler mapper so it applies at creation — earlier
        // and more reliably than tinting the view after layout.
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "TransparentWebViewBackground",
            (handler, view) => handler.PlatformView.SetBackgroundColor(Android.Graphics.Color.Transparent));
#endif

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
        builder.Services.AddScoped<IPrerenderSession, NoOpPrerenderSession>(); // native: no prerender/cookie, localStorage hydration drives it
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

        // Native system-bar tinting (status/nav) so the OS bars follow the in-app light/dark theme.
        // Android-only; other MAUI targets (Windows/iOS/mac) use the no-op.
#if ANDROID
        builder.Services.AddSingleton<IPlatformChrome, AndroidPlatformChrome>();
#else
        builder.Services.AddSingleton<IPlatformChrome, NoOpPlatformChrome>();
#endif

        // ---- LibVLCSharp: software-decodes HEVC/AC3/EAC3/DTS/TrueHD (the audio-codec fix) ----
        // Guarded so a native-load failure — e.g. a Release APK that didn't bundle lib/<abi>/libc++_shared.so
        // for the device's ABI — degrades to "video playback unavailable" instead of hard-crashing the app
        // at launch (the reported Poco F7 crash). Browsing + list tracking stay usable; only the VLC-backed
        // Watch page is affected, and it now fails with a clear message instead of taking the process down.
        var vlcReady = false;
        try
        {
            Core.Initialize();
            vlcReady = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LibVLCSharp Core.Initialize failed — VLC playback disabled: {ex}");
        }
        builder.Services.AddSingleton(_ => vlcReady
            ? new LibVLC()
            : throw new InvalidOperationException("LibVLC native libraries failed to load on this device/build."));
        builder.Services.AddSingleton<IMediaPlayer, VlcMediaPlayer>();

        return builder.Build();
    }
}
