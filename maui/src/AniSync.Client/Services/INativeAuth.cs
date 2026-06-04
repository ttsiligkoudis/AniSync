namespace AniSync.Client.Services;

/// <summary>
/// Native (MAUI) sign-in seam. On the Web head sign-in is a server-side redirect
/// flow (anchors into /Auth/*), so the no-op implementation reports
/// <see cref="IsSupported"/> = false and the shared Login page renders the web
/// anchors/forms. On the MAUI head <c>MauiNativeAuth</c> drives the system browser
/// via <c>WebAuthenticator</c> (OAuth) or posts credentials (Kitsu), then exchanges
/// the result for the config segment — returned here so the page can store it.
/// </summary>
public interface INativeAuth
{
    /// <summary>True on the native head — the Login page should call the methods below
    /// instead of rendering the web redirect anchors/forms.</summary>
    bool IsSupported { get; }

    /// <summary>Runs the provider OAuth flow in the system browser and returns the
    /// resulting config segment (the X-AniSync-Config credential), or null on
    /// failure/cancel. <paramref name="service"/> is the AnimeService name
    /// (Anilist / MyAnimeList / Trakt).</summary>
    Task<string?> StartOAuthAsync(string service);

    /// <summary>Kitsu password-grant sign-in (no browser). Returns the config segment
    /// or null on failure.</summary>
    Task<string?> LoginKitsuAsync(string username, string password);
}

/// <summary>Web-head default — sign-in goes through the server redirect flow, so the
/// Login page uses anchors/forms and never calls these.</summary>
public sealed class NoopNativeAuth : INativeAuth
{
    public bool IsSupported => false;
    public Task<string?> StartOAuthAsync(string service) => Task.FromResult<string?>(null);
    public Task<string?> LoginKitsuAsync(string username, string password) => Task.FromResult<string?>(null);
}
