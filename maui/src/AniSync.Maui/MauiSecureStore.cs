using AniSync.Client.Services;

namespace AniSync.Maui;

/// <summary>MAUI <see cref="ISecureStore"/> backed by platform SecureStorage
/// (Keychain on iOS/macOS, KeyStore on Android, DPAPI on Windows).</summary>
public sealed class MauiSecureStore : ISecureStore
{
    public async Task<string?> GetAsync(string key)
    {
        try { return await SecureStorage.Default.GetAsync(key); }
        catch { return null; }   // SecureStorage can throw on some devices/emulators
    }

    public Task SetAsync(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            SecureStorage.Default.Remove(key);
            return Task.CompletedTask;
        }
        return SecureStorage.Default.SetAsync(key, value);
    }
}
