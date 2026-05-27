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

        public async Task<bool> CreateAsync(NotificationRecord record, IReadOnlyCollection<string> equivalentAnimeIds = null)
        {
            // Build the set of anime ids treated as the same physical anime
            // for dedup. Always includes record.AnimeId; the dispatcher adds
            // every mapped service prefix (anilist:21 / mal:21 / kitsu:11061)
            // so we don't insert a second row when a user flips primary
            // service between two cron pings inside the dispatch lookback.
            var dedupIds = new HashSet<string>(StringComparer.Ordinal) { record.AnimeId };
            if (equivalentAnimeIds != null)
            {
                foreach (var id in equivalentAnimeIds)
                {
                    if (!string.IsNullOrEmpty(id)) dedupIds.Add(id);
                }
            }

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();

            // Dynamic IN-list of equivalent ids. Param count is bounded by
            // AnimeService enum size (3 today), so no risk of hitting
            // SQLite's variable-count cap.
            var inParamNames = new List<string>(dedupIds.Count);
            var idx = 0;
            foreach (var id in dedupIds)
            {
                var name = $"$eqid{idx++}";
                inParamNames.Add(name);
                cmd.Parameters.AddWithValue(name, id);
            }
            var inClause = string.Join(", ", inParamNames);

            // INSERT … SELECT … WHERE NOT EXISTS handles the cross-service
            // duplicate (different anime_id strings for the same physical
            // anime) that the unique index can't catch. OR IGNORE on top
            // catches the exact-match race where two concurrent inserts
            // both pass WHERE NOT EXISTS before either commits.
            cmd.CommandText = $$"""
                INSERT OR IGNORE INTO notifications
                    (uid, service, anime_id, anime_title, episode_number, season,
                     thumbnail_url, link_path, created_at, read_at)
                SELECT $uid, $service, $animeId, $title, $episode, $season,
                       $thumb, $link, $created, NULL
                WHERE NOT EXISTS (
                    SELECT 1 FROM notifications
                    WHERE uid = $uid
                      AND episode_number = $episode
                      AND COALESCE(season, 0) = COALESCE($season, 0)
                      AND anime_id IN ({{inClause}})
                )
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

            // RETURNING yields the inserted id on success and no rows when
            // WHERE NOT EXISTS suppressed the insert (or OR IGNORE caught
            // the unique-index race).
            var insertedId = await cmd.ExecuteScalarAsync();
            if (insertedId == null || insertedId == DBNull.Value) return false;
            record.Id = Convert.ToInt64(insertedId);
            return true;
        }

        public async Task<List<NotificationRecord>> ListForUserAsync(string uid, int limit = 20, int skip = 0)
        {
            if (string.IsNullOrEmpty(uid)) return [];
            if (skip < 0) skip = 0;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, uid, service, anime_id, anime_title, episode_number,
                       season, thumbnail_url, link_path, created_at, read_at
                FROM notifications
                WHERE uid = $uid
                ORDER BY created_at DESC, id DESC
                LIMIT $limit OFFSET $skip;
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$skip", skip);

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
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
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
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
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
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
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

        public async Task<int> MarkManyReadAsync(string uid, IReadOnlyCollection<long> ids)
        {
            if (string.IsNullOrEmpty(uid) || ids == null || ids.Count == 0) return 0;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE notifications SET read_at = $now
                WHERE id = $id AND uid = $uid AND read_at IS NULL;
                """;
            var pId = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var total = 0;
            foreach (var id in ids)
            {
                pId.Value = id;
                total += await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
            return total;
        }

        public async Task<bool> DeleteAsync(string uid, long id)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            // UID gate so user A can't delete user B's rows.
            cmd.CommandText = "DELETE FROM notifications WHERE id = $id AND uid = $uid;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$uid", uid);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        public async Task<int> DeleteManyAsync(string uid, IReadOnlyCollection<long> ids)
        {
            if (string.IsNullOrEmpty(uid) || ids == null || ids.Count == 0) return 0;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM notifications WHERE id = $id AND uid = $uid;";
            var pId = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Parameters.AddWithValue("$uid", uid);

            var total = 0;
            foreach (var id in ids)
            {
                pId.Value = id;
                total += await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
            return total;
        }
    }
}
