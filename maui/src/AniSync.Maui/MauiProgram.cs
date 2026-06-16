using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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

#if ANDROID
        // The TV player view: our ExoVideoView is backed by a Media3 ExoPlayer + PlayerView (Android-only).
        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler(typeof(AniSync.Maui.ExoVideoView), typeof(AniSync.Maui.ExoVideoViewHandler)));
#endif

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

        // Enable HTML5 / iframe fullscreen (the YouTube trailer's fullscreen button, native <video>
        // fullscreen). BlazorWebView sets no WebChromeClient that handles OnShowCustomView, so without
        // this the fullscreen button does nothing. Blazor's interop runs over the WebView message
        // channel, not the chrome client, so swapping the chrome client in is safe.
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "FullscreenVideoSupport",
            (handler, view) => handler.PlatformView.SetWebChromeClient(new FullscreenWebChromeClient()));
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
        // Force IPv4 for the device → AniSync connection. The server resolves the caller's IP and
        // signs IP-locked debrid links to it; the LibVLC player is likewise pinned to IPv4 (--ipv4).
        // On a dual-stack device a link could otherwise be signed over IPv6 (this request) but the
        // video fetched over IPv4 (the player), tripping the debrid "Wrong IP" guard. Keeping both
        // hops on IPv4 makes the signed IP and the playback IP match. Falls back to IPv6 only when
        // the host has no IPv4 route (rare).
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new SocketsHttpHandler { ConnectCallback = ConnectIpv4FirstAsync };
#if DEBUG
            // Trust the local dev HTTPS cert when talking to the local web head.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#endif
            return handler;
        });

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
        // "--ipv4": force libVLC to fetch the stream over IPv4, matching the IPv4 the API HttpClient
        // above signs IP-locked debrid links to — so the signed IP and the playback IP are the same
        // family (avoids the debrid "Wrong IP" error on dual-stack devices).
        builder.Services.AddSingleton(_ => vlcReady
            ? new LibVLC("--ipv4")
            : throw new InvalidOperationException("LibVLC native libraries failed to load on this device/build."));
        builder.Services.AddSingleton<IMediaPlayer, VlcMediaPlayer>();

        return builder.Build();
    }

    // Connect HttpClient sockets over IPv4 when the host has an IPv4 address, falling back to IPv6
    // only if it doesn't. Used so the device → AniSync request (which decides the IP a debrid link is
    // signed to) uses the same IPv4 the LibVLC player fetches the video over.
    private static async ValueTask<Stream> ConnectIpv4FirstAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var ordered = addresses
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1) // IPv4 first
            .ToArray();

        Exception? last = null;
        foreach (var addr in ordered)
        {
            var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addr, port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                last = ex;
                socket.Dispose();
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
