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
        var (found, stale, value) = await ReadAsync<T>(key, ttl);
        return found && !stale ? value : default;
    }

    public Task<(bool Found, bool Stale, T? Value)> ReadAsync<T>(string key, TimeSpan ttl)
    {
        var ttlMs = (long)ttl.TotalMilliseconds;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return ReadCoreAsync<T>(key, ts => Fresh(ts, now, ttlMs));
    }

    public Task<(bool Found, bool Stale, T? Value)> ReadUntilAsync<T>(string key, Func<DateTimeOffset, DateTimeOffset> expiry)
    {
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        return ReadCoreAsync<T>(key, ts =>
            nowMs >= ts                                                            // clock-jumped-back guard
            && now < expiry(DateTimeOffset.FromUnixTimeMilliseconds(ts)));
    }

    // Shared two-tier read; <paramref name="isFresh"/> decides staleness from the entry's write
    // timestamp (sliding TTL or absolute expiry — see the two public readers above).
    private async Task<(bool Found, bool Stale, T? Value)> ReadCoreAsync<T>(string key, Func<long, bool> isFresh)
    {
        // Tier 1 — in-memory (no interop).
        if (_mem.TryGetValue(key, out var mem) && mem.Value is T memHit)
            return (true, !isFresh(mem.Ts), memHit);

        // Tier 2 — localStorage (the first read after an app start lands here; promote into memory).
        try
        {
            var raw = await _js.InvokeAsync<string?>("localStorage.getItem", IClientCache.KeyPrefix + key);
            if (string.IsNullOrEmpty(raw)) return (false, false, default);
            var entry = JsonSerializer.Deserialize<StoredEntry<T>>(raw);
            if (entry is null || entry.Value is null) return (false, false, default);
            _mem[key] = new MemEntry(entry.Ts, entry.Value);
            return (true, !isFresh(entry.Ts), entry.Value);
        }
        catch { return (false, false, default); }   // no JS (prerender) / blocked storage / parse error → miss
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
