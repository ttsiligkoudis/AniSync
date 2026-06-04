namespace AniSync.Client.Services;

/// <summary>
/// Per-host persistent key/value store for small secrets — specifically the
/// user's AniSync config string (the credential for /api/v1/me + the Stremio
/// addon config). MAUI implements it with SecureStorage; the Web head with
/// localStorage via JS interop. Keeps the shared UI from caring how/where the
/// credential is stored.
/// </summary>
public interface ISecureStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string? value);

    /// <summary>Storage key for the AniSync config string.</summary>
    const string ConfigKey = "anisync.config";
}
