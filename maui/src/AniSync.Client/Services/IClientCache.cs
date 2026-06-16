namespace AniSync.Client.Services;

/// <summary>
/// Two-tier client-side cache (in-memory + browser localStorage) with a per-read TTL.
/// Generalises the pattern proven in <c>StatsStrip.razor</c> (timestamp + TTL +
/// <c>System.Text.Json</c> in localStorage) and adds an in-memory tier on top.
///
/// <para>Registered <b>scoped</b>, alongside <see cref="AppState"/>. On the MAUI head the
/// BlazorWebView runs one long-lived DI scope, so the in-memory tier survives all in-app
/// navigation — that's what makes back-navigation (Watch → Detail → Home) free within a
/// session. The localStorage tier survives an app restart.</para>
///
/// <para>All localStorage access is best-effort: during prerender (no JS), or when storage is
/// blocked, reads return <c>null</c> (→ the caller fetches) and writes silently fall back to the
/// in-memory tier — never a broken page. Keys are stored under the <see cref="KeyPrefix"/> so a
/// logout sweep can drop them all.</para>
/// </summary>
public interface IClientCache
{
    /// <summary>Cache key prefix for every entry's localStorage key (<c>anisync.cache.{key}</c>).</summary>
    const string KeyPrefix = "anisync.cache.";

    /// <summary>Return the cached value when present and younger than <paramref name="ttl"/>;
    /// otherwise <c>default</c> (miss / stale / no-JS / parse error). Checks the in-memory tier
    /// first (no interop), then localStorage, promoting a localStorage hit into memory.</summary>
    Task<T?> GetAsync<T>(string key, TimeSpan ttl);

    /// <summary>Store <paramref name="value"/> in both tiers, stamped with the current time.
    /// Best-effort: a blocked/absent localStorage leaves the in-memory tier holding it.</summary>
    Task SetAsync<T>(string key, T value);

    /// <summary>Drop a single entry from both tiers.</summary>
    Task RemoveAsync(string key);

    /// <summary>Drop every entry from both tiers — used on auth transitions so one account's
    /// cached data never leaks to the next.</summary>
    Task ClearAsync();
}
