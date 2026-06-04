using System.Net.Http.Json;
using AniSync.Client.Services;
using Microsoft.Maui.Authentication;

namespace AniSync.Maui;

/// <summary>
/// Native sign-in for the MAUI head. OAuth providers (AniList / MAL / Trakt) open the
/// server flow in the system browser via <see cref="WebAuthenticator"/> against
/// <c>/Auth/Login?...&amp;native=1</c>; on success the server redirects to the app's
/// <c>anisync://auth?code=…</c> deep link, and we exchange that one-time code at
/// <c>/api/v1/auth/native/exchange</c> for the config segment (the X-AniSync-Config
/// credential). Kitsu uses its password grant directly via
/// <c>/api/v1/auth/native/kitsu</c> — no browser. The returned segment is stored by
/// the shared Login page in <see cref="ISecureStore"/>.
///
/// <para><b>Required platform wiring for the <c>anisync://</c> callback scheme:</b></para>
/// <list type="bullet">
///   <item><b>Android</b> — add a <c>WebAuthenticatorCallbackActivity</c> subclass under
///     <c>Platforms/Android</c> with
///     <c>[IntentFilter(new[]{Intent.ActionView}, Categories=new[]{Intent.CategoryDefault, Intent.CategoryBrowsable}, DataScheme="anisync", DataHost="auth")]</c>.</item>
///   <item><b>iOS / Mac Catalyst</b> — add a CFBundleURLTypes entry to <c>Info.plist</c>
///     with URL scheme <c>anisync</c>, and override <c>OpenUrl</c> in the AppDelegate to
///     call <c>Platform.OpenUrl</c> (WebAuthenticator handles the rest via
///     ASWebAuthenticationSession).</item>
///   <item><b>Windows</b> — register the <c>anisync</c> protocol in the app manifest and
///     route activation to <c>WebAuthenticator</c> (see the MAUI WebAuthenticator docs for
///     the WinUI single-instance redirect setup).</item>
/// </list>
/// </summary>
public sealed class MauiNativeAuth : INativeAuth
{
    private readonly IAppEnvironment _env;
    private readonly HttpClient _http;

    public MauiNativeAuth(IAppEnvironment env, IHttpClientFactory httpFactory)
    {
        _env = env;
        _http = httpFactory.CreateClient();
    }

    public bool IsSupported => true;

    public async Task<string?> StartOAuthAsync(string service)
    {
        var baseUrl = _env.ApiBaseUrl.TrimEnd('/');
        var authUrl = new Uri($"{baseUrl}/Auth/Login?animeService={Uri.EscapeDataString(service)}&native=1");
        var callback = new Uri("anisync://auth");
        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(authUrl, callback);
            if (result.Properties.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code))
                return await ExchangeAsync(code);
        }
        catch (TaskCanceledException) { /* user dismissed the browser */ }
        catch { /* network / provider error — surfaced as a failed sign-in */ }
        return null;
    }

    public async Task<string?> LoginKitsuAsync(string username, string password)
        => await PostForConfigAsync("/api/v1/auth/native/kitsu", new { username, password });

    private Task<string?> ExchangeAsync(string code)
        => PostForConfigAsync("/api/v1/auth/native/exchange", new { code });

    private async Task<string?> PostForConfigAsync(string path, object body)
    {
        var baseUrl = _env.ApiBaseUrl.TrimEnd('/');
        try
        {
            using var resp = await _http.PostAsJsonAsync($"{baseUrl}{path}", body);
            if (!resp.IsSuccessStatusCode) return null;
            var dto = await resp.Content.ReadFromJsonAsync<ConfigResult>();
            return dto?.config;
        }
        catch { return null; }
    }

    private sealed class ConfigResult { public string? config { get; set; } }
}
