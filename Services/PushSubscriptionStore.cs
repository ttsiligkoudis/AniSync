using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Data.Sqlite;

namespace AnimeList.Services
{
    /// <summary>
    /// SQLite-backed <see cref="IPushSubscriptionStore"/>. Shares
    /// <c>anisync.db</c> with the other stores; the
    /// <c>push_subscriptions</c> table is created by
    /// <see cref="SqliteConfigStore.EnsureSchema"/> on startup.
    /// </summary>
    public class PushSubscriptionStore : IPushSubscriptionStore
    {
        private readonly string _connectionString;

        public PushSubscriptionStore(IConfiguration configuration)
        {
            _connectionString = SqliteConnectionFactory.BuildConnectionString(configuration);
        }

        public async Task UpsertAsync(PushSubscriptionRecord record)
        {
            if (record == null
                || string.IsNullOrEmpty(record.Uid)
                || string.IsNullOrEmpty(record.Endpoint))
            {
                return;
            }

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO push_subscriptions
                    (uid, endpoint, p256dh, auth, user_agent, created_at)
                VALUES
                    ($uid, $endpoint, $p256dh, $auth, $ua, $now)
                ON CONFLICT(uid, endpoint) DO UPDATE SET
                    p256dh     = excluded.p256dh,
                    auth       = excluded.auth,
                    user_agent = excluded.user_agent,
                    created_at = excluded.created_at;
                """;
            cmd.Parameters.AddWithValue("$uid", record.Uid);
            cmd.Parameters.AddWithValue("$endpoint", record.Endpoint);
            cmd.Parameters.AddWithValue("$p256dh", record.P256dh);
            cmd.Parameters.AddWithValue("$auth", record.Auth);
            cmd.Parameters.AddWithValue("$ua", (object)record.UserAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<PushSubscriptionRecord>> ListForUserAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return [];
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, uid, endpoint, p256dh, auth, user_agent, created_at
                FROM push_subscriptions
                WHERE uid = $uid;
                """;
            cmd.Parameters.AddWithValue("$uid", uid);

            var result = new List<PushSubscriptionRecord>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new PushSubscriptionRecord
                {
                    Id = reader.GetInt64(0),
                    Uid = reader.GetString(1),
                    Endpoint = reader.GetString(2),
                    P256dh = reader.GetString(3),
                    Auth = reader.GetString(4),
                    UserAgent = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreatedAt = reader.GetInt64(6),
                });
            }
            return result;
        }

        public async Task<bool> RemoveByEndpointAsync(string uid, string endpoint)
        {
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(endpoint)) return false;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM push_subscriptions WHERE uid = $uid AND endpoint = $endpoint;";
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$endpoint", endpoint);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        public async Task<bool> DeleteAsync(long id)
        {
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM push_subscriptions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        public async Task<bool> HasAnyAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM push_subscriptions WHERE uid = $uid LIMIT 1;";
            cmd.Parameters.AddWithValue("$uid", uid);
            var result = await cmd.ExecuteScalarAsync();
            return result != null && result != DBNull.Value;
        }
    }
}
