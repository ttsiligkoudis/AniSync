using AnimeList.Services.Interfaces;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace AnimeList.Services
{
    /// <summary>
    /// SQLite-backed <see cref="IWatchingCacheStore"/>. Shares
    /// <c>anisync.db</c> with the other stores; the
    /// <c>user_watching_cache</c> table is created by
    /// <see cref="SqliteConfigStore.EnsureSchema"/> on startup.
    /// </summary>
    public class WatchingCacheStore : IWatchingCacheStore
    {
        private readonly string _connectionString;

        public WatchingCacheStore(IConfiguration configuration)
        {
            var dataDir = configuration["ANISYNC_DATA_DIR"]
                ?? Environment.GetEnvironmentVariable("ANISYNC_DATA_DIR")
                ?? ".";
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDir, "anisync.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                Cache = SqliteCacheMode.Shared,
            }.ToString();
        }

        public async Task UpsertAsync(string uid, IReadOnlyCollection<string> mediaIds, AnimeService service)
        {
            if (string.IsNullOrEmpty(uid)) return;
            var json = JsonConvert.SerializeObject(mediaIds ?? []);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO user_watching_cache
                    (uid, service, media_ids_json, refreshed_at, last_error, last_error_at)
                VALUES
                    ($uid, $service, $json, $now, NULL, NULL)
                ON CONFLICT(uid) DO UPDATE SET
                    service        = excluded.service,
                    media_ids_json = excluded.media_ids_json,
                    refreshed_at   = excluded.refreshed_at,
                    last_error     = NULL,
                    last_error_at  = NULL;
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$service", (int)service);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<WatchingCacheEntry> GetAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT uid, service, media_ids_json, refreshed_at
                FROM user_watching_cache
                WHERE uid = $uid;
                """;
            cmd.Parameters.AddWithValue("$uid", uid);

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return BuildEntry(reader);
        }

        public async Task<List<string>> GetStaleUidsAsync(TimeSpan maxAge, int limit)
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge).ToUnixTimeSeconds();
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // LEFT JOIN configs so users with no cache row yet (first-run case)
            // are returned and prioritised — and so explicitly-invalidated rows
            // (refreshed_at = 0) sort to the front via COALESCE.
            cmd.CommandText = """
                SELECT c.uid
                FROM configs c
                LEFT JOIN user_watching_cache w ON w.uid = c.uid
                WHERE w.refreshed_at IS NULL OR w.refreshed_at < $cutoff
                ORDER BY COALESCE(w.refreshed_at, 0) ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$limit", limit);

            var result = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(reader.GetString(0));
            return result;
        }

        public async Task<List<WatchingCacheEntry>> GetAllAsync()
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT uid, service, media_ids_json, refreshed_at
                FROM user_watching_cache;
                """;

            var result = new List<WatchingCacheEntry>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(BuildEntry(reader));
            return result;
        }

        public async Task MarkErrorAsync(string uid, string error)
        {
            if (string.IsNullOrEmpty(uid)) return;
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Upsert error into a row even if the user has never had a
            // successful refresh — keeps a record so we don't retry the same
            // broken token every tick before the staleness gate trips. Bumps
            // refreshed_at so the row sorts out of the next stale-uid query;
            // a subsequent successful Upsert will replace it.
            cmd.CommandText = """
                INSERT INTO user_watching_cache
                    (uid, service, media_ids_json, refreshed_at, last_error, last_error_at)
                VALUES
                    ($uid, 0, '[]', $now, $err, $now)
                ON CONFLICT(uid) DO UPDATE SET
                    refreshed_at  = excluded.refreshed_at,
                    last_error    = excluded.last_error,
                    last_error_at = excluded.last_error_at;
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$err", error ?? string.Empty);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task MarkStaleAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Zero out refreshed_at so GetStaleUidsAsync surfaces this row
            // immediately on the next refresh pass. No-op if the row doesn't
            // exist yet — the user has never had a cache populated, and the
            // LEFT JOIN in GetStaleUidsAsync handles that case anyway.
            cmd.CommandText = "UPDATE user_watching_cache SET refreshed_at = 0 WHERE uid = $uid;";
            cmd.Parameters.AddWithValue("$uid", uid);
            await cmd.ExecuteNonQueryAsync();
        }

        private static WatchingCacheEntry BuildEntry(SqliteDataReader reader)
        {
            var uid = reader.GetString(0);
            var service = (AnimeService)reader.GetInt32(1);
            var rawJson = reader.GetString(2);
            var refreshedAt = reader.GetInt64(3);
            HashSet<string> ids;
            try
            {
                ids = JsonConvert.DeserializeObject<HashSet<string>>(rawJson) ?? [];
            }
            catch
            {
                ids = [];
            }
            return new WatchingCacheEntry(uid, service, ids, refreshedAt);
        }
    }
}
