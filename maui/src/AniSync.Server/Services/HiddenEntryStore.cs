using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// SQLite-backed <see cref="IHiddenEntryStore"/>. Shares the same
    /// <c>anisync.db</c> file as <see cref="SqliteConfigStore"/>; the
    /// <c>hidden_entries</c> table is created by that store's
    /// <c>EnsureSchema</c> on startup, so this store assumes it exists.
    /// </summary>
    public class HiddenEntryStore : IHiddenEntryStore
    {
        private readonly string _connectionString;

        public HiddenEntryStore(IConfiguration configuration)
        {
            _connectionString = SqliteConnectionFactory.BuildConnectionString(configuration);
        }

        public async Task AddAsync(string uid, HiddenEntry entry)
        {
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(entry?.Id)) return;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            // ON CONFLICT refreshes the cached display fields (title/poster/type)
            // without disturbing the original created_at, so re-hiding keeps the
            // entry's place in the most-recently-hidden ordering stable while
            // still picking up a renamed title or updated poster.
            cmd.CommandText = """
                INSERT INTO hidden_entries (uid, id, title, image_url, media_type, created_at)
                VALUES ($uid, $id, $title, $image, $type, $created)
                ON CONFLICT(uid, id) DO UPDATE SET
                    title = excluded.title,
                    image_url = excluded.image_url,
                    media_type = excluded.media_type;
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$title", (object)entry.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$image", (object)entry.ImageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$type", (object)entry.MediaType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<bool> RemoveAsync(string uid, string id)
        {
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(id)) return false;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM hidden_entries WHERE uid = $uid AND id = $id;";
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$id", id);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        public async Task<bool> IsHiddenAsync(string uid, string id)
        {
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(id)) return false;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM hidden_entries WHERE uid = $uid AND id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$id", id);
            return await cmd.ExecuteScalarAsync() != null;
        }

        public async Task<HashSet<string>> GetHiddenIdsAsync(string uid)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(uid)) return set;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM hidden_entries WHERE uid = $uid;";
            cmd.Parameters.AddWithValue("$uid", uid);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                set.Add(reader.GetString(0));
            return set;
        }

        public async Task<List<HiddenEntry>> GetPageAsync(string uid, int limit, int offset, string mediaType = null)
        {
            var result = new List<HiddenEntry>();
            if (string.IsNullOrEmpty(uid)) return result;
            if (offset < 0) offset = 0;
            if (limit < 1) limit = 1;
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            // Optional media-type filter so the Hidden view can show only the active mode. Anime also
            // matches legacy NULL rows (older hides were anime-only and stored no type); movie/series
            // match their exact stored type. Done in SQL so paging stays correct.
            var typeFilter = mediaType switch
            {
                "anime" => " AND (media_type = 'anime' OR media_type IS NULL)",
                "movie" or "series" => " AND media_type = $type",
                _ => "",
            };
            cmd.CommandText = $"""
                SELECT id, title, image_url, media_type, created_at
                FROM hidden_entries
                WHERE uid = $uid{typeFilter}
                ORDER BY created_at DESC, rowid DESC
                LIMIT $limit OFFSET $offset;
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            if (mediaType is "movie" or "series") cmd.Parameters.AddWithValue("$type", mediaType);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new HiddenEntry
                {
                    Id = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? null : reader.GetString(1),
                    ImageUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                    MediaType = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAt = reader.GetInt64(4),
                });
            }
            return result;
        }
    }
}
