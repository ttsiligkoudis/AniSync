using System.Collections.Concurrent;

namespace AnimeList.Services
{
    /// <summary>
    /// Short-lived one-time codes that bridge a server-side OAuth login (run inside
    /// the native app's system browser) back to the app without putting the config
    /// credential in the deep-link URL. The OAuth callback issues a code mapped to the
    /// resolved row UID and redirects to <c>anisync://auth?code=…</c>; the app then
    /// POSTs the code to <c>/api/v1/auth/native/exchange</c> and gets its config
    /// segment. Codes are single-use and expire fast — they never carry secrets
    /// themselves, only a lookup into this in-memory map.
    /// </summary>
    public interface INativeAuthCodeStore
    {
        /// <summary>Mints a one-time code for the UID. Valid for ~2 minutes.</summary>
        string Issue(string uid);

        /// <summary>Redeems and removes a code, returning its UID (or null if unknown/expired).</summary>
        string Redeem(string code);
    }

    public sealed class NativeAuthCodeStore : INativeAuthCodeStore
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
        private readonly ConcurrentDictionary<string, (string Uid, DateTime Expires)> _codes = new();

        public string Issue(string uid)
        {
            Sweep();
            // Reuse the CSPRNG, URL-safe token generator the OAuth `state` uses.
            var code = Utils.GenerateCodeVerifier();
            _codes[code] = (uid, DateTime.UtcNow.Add(Ttl));
            return code;
        }

        public string Redeem(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (_codes.TryRemove(code, out var entry) && entry.Expires > DateTime.UtcNow)
                return entry.Uid;
            return null;
        }

        // Bounded by the 2-minute TTL × in-flight native logins, so an opportunistic
        // sweep on Issue keeps the map tiny without a background timer.
        private void Sweep()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _codes)
                if (kv.Value.Expires <= now)
                    _codes.TryRemove(kv.Key, out _);
        }
    }
}
