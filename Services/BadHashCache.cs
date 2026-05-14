using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Singleton snapshot over the SQLite <c>bad_hashes</c> table.
    /// Hosts the in-memory refresh-tick that both the addon stream
    /// fetcher and the resolve-stream mark path consult — previously
    /// duplicated inside TorrentioService when it owned the snapshot.
    /// </summary>
    public class BadHashCache : IBadHashCache
    {
        private readonly IConfigStore _configStore;
        private readonly ILogger<BadHashCache> _logger;

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MarkTtl = TimeSpan.FromHours(1);
        private static readonly SemaphoreSlim _refreshGate = new(1, 1);

        private static volatile HashSet<string> _snapshot = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _loadedAt = DateTime.MinValue;

        public BadHashCache(IConfigStore configStore, ILogger<BadHashCache> logger)
        {
            _configStore = configStore;
            _logger = logger;
        }

        public async Task<HashSet<string>> GetSnapshotAsync()
        {
            if (DateTime.UtcNow - _loadedAt < RefreshInterval)
                return _snapshot;

            await _refreshGate.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _loadedAt < RefreshInterval)
                    return _snapshot;

                try
                {
                    var live = await _configStore.GetActiveBadHashesAsync(DateTime.UtcNow);
                    _snapshot = new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
                    _loadedAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh bad-hash list — using stale snapshot.");
                }
                return _snapshot;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        public async Task MarkAsync(string infoHash)
        {
            if (string.IsNullOrEmpty(infoHash) || infoHash.Length != 40) return;
            var key = infoHash.ToLowerInvariant();
            try
            {
                await _configStore.MarkBadHashAsync(key, DateTime.UtcNow + MarkTtl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist bad hash {Hash}; keeping in-memory only.", key);
            }

            // Copy-on-write so concurrent readers iterating the snapshot
            // don't observe a partially-updated set.
            var updated = new HashSet<string>(_snapshot, StringComparer.OrdinalIgnoreCase) { key };
            _snapshot = updated;
        }
    }
}
