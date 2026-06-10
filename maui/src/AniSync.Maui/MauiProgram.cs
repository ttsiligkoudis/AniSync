using System.Net.Http;
using Microsoft.Extensions.Logging;
using AniSync.Client.Services;
using AniSync.Maui;
using LibVLCSharp.MAUI;
using LibVLCSharp.Shared;

namespace AniSync;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            // Registers the LibVLCSharp.MAUI VideoView handler. Without this, presenting the player page
            // throws HandlerNotFoundException for VideoView (the debrid-playback crash): audio started but
            // the video view couldn't be created.
            .UseLibVLCSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                // Material Icons — uniform-size player control glyphs (Stremio-style chrome).
                fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
            });

        builder.Services.AddMauiBlazorWebView();

#if ANDROID
        // Paint the Android WebView's background to the THEME colour the moment its handler creates it.
        // Chromium renders the document white before the page's CSS background paints, flashing white on
        // cold start / resume / app-switch (obvious in dark mode); transparent didn't mask it, so use an
        // opaque themed colour. Night mode is forced to the in-app theme (MainApplication), so the device
        // config here reflects the app's light/dark choice.
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "ThemedWebViewBackground",
            (handler, view) =>
            {
                var ctx = handler.PlatformView.Context;
                var night = ctx is not null
                    && (ctx.Resources!.Configuration!.UiMode & Android.Content.Res.UiMode.NightMask)
                       == Android.Content.Res.UiMode.NightYes;
                handler.PlatformView.SetBackgroundColor(
                    Android.Graphics.Color.ParseColor(night ? "#0A0A0A" : "#FFFFFF"));
            });
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
