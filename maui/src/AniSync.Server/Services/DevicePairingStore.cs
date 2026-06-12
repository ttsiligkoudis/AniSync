using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AnimeList.Services
{
    /// <summary>
    /// In-memory rendezvous for the TV "scan a QR on your phone" sign-in (an
    /// RFC 8628-style device-authorization flow, the same pattern Netflix / Stremio
    /// use). The TV starts a pairing and gets back a high-entropy <c>DeviceCode</c>
    /// (secret, only the TV ever holds it — used to poll) plus a short, human
    /// <c>UserCode</c> (shown on screen + encoded in the QR's /link URL). The phone,
    /// once signed in, approves the UserCode; the next TV poll then yields the
    /// account's config segment exactly once.
    ///
    /// Mirrors <see cref="NativeAuthCodeStore"/>: a singleton-backed
    /// ConcurrentDictionary with an opportunistic sweep (no background timer) — the
    /// short TTL keeps the map tiny. This lives in process memory, which is fine for
    /// the single-instance Fly deployment (same assumption the SQLite config store
    /// and NativeAuthCodeStore already make); a multi-instance deploy would need a
    /// shared/sticky store.
    /// </summary>
    public interface IDevicePairingStore
    {
        /// <summary>Creates a pending pairing and returns the codes to show on the TV.</summary>
        DevicePairingTicket Create(TimeSpan ttl);

        /// <summary>True if a non-expired pending pairing exists for the (normalized) user code.</summary>
        bool Exists(string userCode);

        /// <summary>Binds the signed-in account UID to the user code. False if the code is
        /// unknown/expired/already used.</summary>
        bool TryApprove(string userCode, string uid);

        /// <summary>TV poll. Returns the current state for the device code; on the first
        /// poll after approval it returns <see cref="DevicePairingStatus.Approved"/> with the
        /// UID and consumes the entry so the config can't be replayed.</summary>
        DevicePollOutcome Poll(string deviceCode);
    }

    public enum DevicePairingStatus { Pending, Approved, Expired }

    public sealed record DevicePairingTicket(string DeviceCode, string UserCode, DateTime Expires);

    public sealed record DevicePollOutcome(DevicePairingStatus Status, string Uid);

    public sealed class DevicePairingStore : IDevicePairingStore
    {
        private sealed class Entry
        {
            public string UserCode = "";
            public string Uid;          // null until the phone approves
            public bool Approved;
            public DateTime Expires;
        }

        // user_code alphabet: uppercase, no 0/O/1/I/L so it's unambiguous if a user has to
        // read it off the TV and type it manually (the QR carries it for the common path).
        private const string UserCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        private const int UserCodeLength = 8;

        // Keyed by the secret device_code (what the TV polls with). A reverse index maps the
        // public user_code → device_code so the phone's approval can find the entry.
        private readonly ConcurrentDictionary<string, Entry> _byDevice = new();
        private readonly ConcurrentDictionary<string, string> _userToDevice = new(StringComparer.Ordinal);

        public DevicePairingTicket Create(TimeSpan ttl)
        {
            Sweep();
            var deviceCode = Utils.GenerateCodeVerifier();          // CSPRNG, URL-safe, ~43 chars
            var userCode = NewUserCode();
            var expires = DateTime.UtcNow.Add(ttl);

            _byDevice[deviceCode] = new Entry { UserCode = userCode, Expires = expires };
            _userToDevice[userCode] = deviceCode;
            return new DevicePairingTicket(deviceCode, userCode, expires);
        }

        public bool Exists(string userCode)
        {
            var code = Normalize(userCode);
            return code is not null
                && _userToDevice.TryGetValue(code, out var device)
                && _byDevice.TryGetValue(device, out var e)
                && e.Expires > DateTime.UtcNow
                && !e.Approved;
        }

        public bool TryApprove(string userCode, string uid)
        {
            var code = Normalize(userCode);
            if (code is null || string.IsNullOrEmpty(uid)) return false;
            if (!_userToDevice.TryGetValue(code, out var device)) return false;
            if (!_byDevice.TryGetValue(device, out var e)) return false;
            if (e.Approved || e.Expires <= DateTime.UtcNow) return false;

            e.Uid = uid;
            e.Approved = true;
            return true;
        }

        public DevicePollOutcome Poll(string deviceCode)
        {
            if (string.IsNullOrEmpty(deviceCode) || !_byDevice.TryGetValue(deviceCode, out var e))
                return new DevicePollOutcome(DevicePairingStatus.Expired, null);

            if (e.Expires <= DateTime.UtcNow)
            {
                Remove(deviceCode, e.UserCode);
                return new DevicePollOutcome(DevicePairingStatus.Expired, null);
            }

            if (!e.Approved)
                return new DevicePollOutcome(DevicePairingStatus.Pending, null);

            // Approved: hand the UID over exactly once, then burn the entry.
            Remove(deviceCode, e.UserCode);
            return new DevicePollOutcome(DevicePairingStatus.Approved, e.Uid);
        }

        private void Remove(string deviceCode, string userCode)
        {
            _byDevice.TryRemove(deviceCode, out _);
            if (userCode is not null) _userToDevice.TryRemove(userCode, out _);
        }

        private string NewUserCode()
        {
            // Avoid collisions with an in-flight pairing (vanishingly unlikely, but cheap).
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var code = RandomCode();
                if (!_userToDevice.ContainsKey(code)) return code;
            }
            return RandomCode();
        }

        private static string RandomCode()
        {
            var chars = new char[UserCodeLength];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
            return new string(chars);
        }

        /// <summary>Strips spaces/dashes and upppercases so "abcd-efgh" entered on a phone
        /// matches the stored "ABCDEFGH".</summary>
        private static string Normalize(string userCode)
        {
            if (string.IsNullOrWhiteSpace(userCode)) return null;
            Span<char> buf = stackalloc char[userCode.Length];
            var n = 0;
            foreach (var ch in userCode)
            {
                if (ch is ' ' or '-' or '_') continue;
                buf[n++] = char.ToUpperInvariant(ch);
            }
            return n == 0 ? null : new string(buf[..n]);
        }

        private void Sweep()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _byDevice)
                if (kv.Value.Expires <= now)
                    Remove(kv.Key, kv.Value.UserCode);
        }
    }
}
