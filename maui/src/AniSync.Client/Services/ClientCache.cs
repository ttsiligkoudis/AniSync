using System.Text.Json;
using Microsoft.JSInterop;

namespace AniSync.Client.Services;

/// <inheritdoc cref="IClientCache"/>
public sealed class ClientCache : IClientCache
{
    private readonly IJSRuntime _js;

    // In-memory tier: the deserialised value is held boxed so a hit costs no JSON work — the
    // whole point of this tier on a low-power TV. Ordinal keys (they're code-defined, not text).
    private readonly Dictionary<string, MemEntry> _mem = new(StringComparer.Ordinal);
    private sealed record MemEntry(long Ts, object? Value);

    // localStorage payload: timestamp + value, same shape as StatsStrip's AnimeStatsCache.
    private sealed record StoredEntry<T>(long Ts, T Value);

    public ClientCache(IJSRuntime js) => _js = js;

    public async Task<T?> GetAsync<T>(string key, TimeSpan ttl)
    {
        var ttlMs = (long)ttl.TotalMilliseconds;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Tier 1 — in-memory (no interop). A stale entry falls through to localStorage, which on a
        // fresh app start (memory empty) is also where the first read lands.
        if (_mem.TryGetValue(key, out var mem) && Fresh(mem.Ts, now, ttlMs) && mem.Value is T hit)
            return hit;

        // Tier 2 — localStorage.
        try
        {
            var raw = await _js.InvokeAsync<string?>("localStorage.getItem", IClientCache.KeyPrefix + key);
            if (string.IsNullOrEmpty(raw)) return default;
            var entry = JsonSerializer.Deserialize<StoredEntry<T>>(raw);
            if (entry is null || entry.Value is null || !Fresh(entry.Ts, now, ttlMs)) return default;
            _mem[key] = new MemEntry(entry.Ts, entry.Value);   // promote so the next read skips interop
            return entry.Value;
        }
        catch { return default; }   // no JS (prerender) / blocked storage / parse error → treat as miss
    }

    public async Task SetAsync<T>(string key, T value)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _mem[key] = new MemEntry(ts, value);
        try
        {
            var json = JsonSerializer.Serialize(new StoredEntry<T>(ts, value));
            await _js.InvokeVoidAsync("localStorage.setItem", IClientCache.KeyPrefix + key, json);
        }
        catch { /* storage blocked → the in-memory tier still holds it for this session */ }
    }

    public async Task RemoveAsync(string key)
    {
        _mem.Remove(key);
        try { await _js.InvokeVoidAsync("localStorage.removeItem", IClientCache.KeyPrefix + key); } catch { }
    }

    public async Task ClearAsync()
    {
        _mem.Clear();
        // Sweep every localStorage key under our prefix. Imported lazily — only auth transitions
        // hit this, so we don't load the module on the hot path.
        try
        {
            await using var mod = await _js.InvokeAsync<IJSObjectReference>(
                "import", "./_content/AniSync.Client/js/client-cache.js");
            await mod.InvokeVoidAsync("clearPrefix", IClientCache.KeyPrefix);
        }
        catch { /* no JS → memory tier already cleared, which is all that matters this session */ }
    }

    // age >= 0 guards against a clock that jumped backwards (negative age → treat as stale/refetch),
    // matching StatsStrip's check.
    private static bool Fresh(long ts, long now, long ttlMs)
    {
        var age = now - ts;
        return age >= 0 && age < ttlMs;
    }
}
