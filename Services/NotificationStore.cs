using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Data.Sqlite;

namespace AnimeList.Services
{
    /// <summary>
    /// SQLite-backed <see cref="INotificationStore"/>. Shares the same
    /// <c>anisync.db</c> file as <see cref="SqliteConfigStore"/>; schema for
    /// the <c>notifications</c> table is created by that store's
    /// <c>EnsureSchema</c> on startup, so this store assumes the table exists.
    /// </summary>
    public class NotificationStore : INotificationStore
    {
        private readonly string _connectionString;

        public NotificationStore(IConfiguration configuration)
        {
            _connectionString = SqliteConnectionFactory.BuildConnectionString(configuration);
        }

        public async Task<bool> CreateAsync(NotificationRecord record)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO notifications
                    (uid, service, anime_id, anime_title, episode_number, season,
                     thumbnail_url, link_path, created_at, read_at)
                VALUES
                    ($uid, $service, $animeId, $title, $episode, $season,
                     $thumb, $link, $created, NULL)
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$uid", record.Uid);
            cmd.Parameters.AddWithValue("$service", (int)record.Service);
            cmd.Parameters.AddWithValue("$animeId", record.AnimeId);
            cmd.Parameters.AddWithValue("$title", record.AnimeTitle);
            cmd.Parameters.AddWithValue("$episode", record.EpisodeNumber);
            cmd.Parameters.AddWithValue("$season", (object)record.Season ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumb", (object)record.ThumbnailUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$link", record.LinkPath);
            cmd.Parameters.AddWithValue("$created", record.CreatedAt);

            // RETURNING returns the inserted row's id on success and zero rows
            // on conflict (because OR IGNORE swallowed the insert).
            var insertedId = await cmd.ExecuteScalarAsync();
            if (insertedId == null || insertedId == DBNull.Value) return false;
            record.Id = Convert.ToInt64(insertedId);
            return true;
        }

        public async Task<List<NotificationRecord>> ListForUserAsync(string uid, int limit = 20)
        {
            if (string.IsNullOrEmpty(uid)) return [];
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, uid, service, anime_id, anime_title, episode_number,
                       season, thumbnail_url, link_path, created_at, read_at
                FROM notifications
                WHERE uid = $uid
                ORDER BY created_at DESC, id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$limit", limit);

            var result = new List<NotificationRecord>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new NotificationRecord
                {
                    Id = reader.GetInt64(0),
                    Uid = reader.GetString(1),
                    Service = (AnimeService)reader.GetInt32(2),
                    AnimeId = reader.GetString(3),
                    AnimeTitle = reader.GetString(4),
                    EpisodeNumber = reader.GetInt32(5),
                    Season = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    ThumbnailUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
                    LinkPath = reader.GetString(8),
                    CreatedAt = reader.GetInt64(9),
                    ReadAt = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                });
            }
            return result;
        }

        public async Task<int> GetUnreadCountAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return 0;
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Hits the partial index idx_notifications_unread.
            cmd.CommandText = "SELECT COUNT(*) FROM notifications WHERE uid = $uid AND read_at IS NULL;";
            cmd.Parameters.AddWithValue("$uid", uid);
            var raw = await cmd.ExecuteScalarAsync();
            return raw == null ? 0 : Convert.ToInt32(raw);
        }

        public async Task<bool> MarkReadAsync(string uid, long id)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // uid gate is the authorization boundary — a notification id alone
            // mustn't be enough to mark another user's row as read.
            cmd.CommandText = """
                UPDATE notifications
                SET read_at = $now
                WHERE id = $id AND uid = $uid AND read_at IS NULL;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var affected = await cmd.ExecuteNonQueryAsync();
            return affected > 0;
        }

        public async Task<int> MarkAllReadAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return 0;
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE notifications
                SET read_at = $now
                WHERE uid = $uid AND read_at IS NULL;
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync();
        }
    }
}
