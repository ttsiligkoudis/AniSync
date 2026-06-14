using System.Collections.Concurrent;

namespace AnimeList.Services
{
    /// <summary>
    /// In-memory rendezvous for the TV → phone "manage settings on your phone" handoff.
    /// Editing settings (addon URLs, debrid keys) with a D-pad is painful, so a signed-in
    /// TV mints a single-use, short-lived token bound to its OWN account; the phone opens
    /// the QR's /tv/handoff URL, which redeems the token and signs that browser in as the
    /// account before landing on the settings page.
    ///
    /// This is the reverse direction of <see cref="DevicePairingStore"/> (phone → TV): there
    /// the phone approves a code the TV polls; here the already-signed-in TV hands its account
    /// to the phone. The token is a bearer capability — high-entropy, single-use and short-lived
    /// so a stale/leaked QR can't be replayed. Like the device-pairing + native-code stores it's
    /// a singleton ConcurrentDictionary with an opportunistic sweep (fine for the single-instance
    /// Fly deployment; a multi-instance deploy would need a shared store).
    /// </summary>
    public interface ISettingsHandoffStore
    {
        /// <summary>Mints a single-use token for <paramref name="uid"/>, valid for <paramref name="ttl"/>.</summary>
        string Create(string uid, TimeSpan ttl);

        /// <summary>Redeems a token, returning its account UID exactly once. Null when the token
        /// is unknown, expired or already redeemed.</summary>
        string Redeem(string token);
    }

    public sealed class SettingsHandoffStore : ISettingsHandoffStore
    {
        private sealed record Entry(string Uid, DateTime Expires);

        private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

        public string Create(string uid, TimeSpan ttl)
        {
            Sweep();
            var token = Utils.GenerateCodeVerifier();   // CSPRNG, URL-safe, ~43 chars
            _tokens[token] = new Entry(uid, DateTime.UtcNow.Add(ttl));
            return token;
        }

        public string Redeem(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            // TryRemove makes redemption atomic + single-use: the first caller gets the uid,
            // a replay finds nothing.
            if (!_tokens.TryRemove(token, out var e)) return null;
            return e.Expires > DateTime.UtcNow ? e.Uid : null;
        }

        private void Sweep()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _tokens)
                if (kv.Value.Expires <= now)
                    _tokens.TryRemove(kv.Key, out _);
        }
    }
}
