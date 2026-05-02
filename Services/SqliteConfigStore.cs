using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace AnimeList.Services
{
    /// <summary>
    /// SQLite-backed implementation of <see cref="IConfigStore"/>. Single file, ACID, opens one
    /// pooled connection string and lets ADO.NET multiplex. Schema is created on first use.
    /// </summary>
    public class SqliteConfigStore : IConfigStore
    {
        private readonly string _connectionString;

        public SqliteConfigStore(IConfiguration configuration)
        {
            // Honour ANISYNC_DATA_DIR (Fly.io sets it to /data) and fall back to the working
            // directory, which is enough for local dev. Make sure the directory exists so the
            // SQLite open call doesn't fail with "unable to open database file".
            var dataDir = configuration["ANISYNC_DATA_DIR"]
                ?? Environment.GetEnvironmentVariable("ANISYNC_DATA_DIR")
                ?? ".";
            Directory.CreateDirectory(dataDir);

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDir, "anisync.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                Cache = SqliteCacheMode.Shared,
            }.ToString();

            EnsureSchema();
        }

        private void EnsureSchema()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS configs (
                    uid         TEXT PRIMARY KEY,
                    service     INTEGER NOT NULL,
                    user_key    TEXT,
                    token_json  TEXT NOT NULL,
                    created_at  INTEGER NOT NULL,
                    updated_at  INTEGER NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_configs_user
                    ON configs(service, user_key)
                    WHERE user_key IS NOT NULL;
                """;
            cmd.ExecuteNonQuery();
        }

        public async Task<string> UpsertAsync(TokenData tokenData)
        {
            var serviceInt = (int)tokenData.anime_service;
            var userKey = GetUserKey(tokenData);
            var json = SerializeObject(tokenData);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            // Try to update an existing row keyed on identity. Anonymous (no user_key) always
            // creates a new row — they're rare and we don't try to deduplicate.
            if (!string.IsNullOrEmpty(userKey))
            {
                using var update = conn.CreateCommand();
                update.CommandText = """
                    UPDATE configs
                       SET token_json = $j, updated_at = $u
                     WHERE service = $s AND user_key = $k
                    RETURNING uid
                    """;
                update.Parameters.AddWithValue("$j", json);
                update.Parameters.AddWithValue("$u", now);
                update.Parameters.AddWithValue("$s", serviceInt);
                update.Parameters.AddWithValue("$k", userKey);
                var existing = await update.ExecuteScalarAsync() as string;
                if (!string.IsNullOrEmpty(existing)) return existing;
            }

            var newUid = GenerateUid();
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO configs (uid, service, user_key, token_json, created_at, updated_at)
                VALUES ($uid, $s, $k, $j, $c, $u)
                """;
            insert.Parameters.AddWithValue("$uid", newUid);
            insert.Parameters.AddWithValue("$s", serviceInt);
            insert.Parameters.AddWithValue("$k", (object)userKey ?? DBNull.Value);
            insert.Parameters.AddWithValue("$j", json);
            insert.Parameters.AddWithValue("$c", now);
            insert.Parameters.AddWithValue("$u", now);
            await insert.ExecuteNonQueryAsync();
            return newUid;
        }

        public async Task<TokenData> GetAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT token_json FROM configs WHERE uid = $uid LIMIT 1";
            cmd.Parameters.AddWithValue("$uid", uid);
            var json = await cmd.ExecuteScalarAsync() as string;
            if (string.IsNullOrEmpty(json)) return null;

            return DeserializeObject<TokenData>(json);
        }

        public async Task UpdateByUserAsync(TokenData tokenData)
        {
            var userKey = GetUserKey(tokenData);
            if (string.IsNullOrEmpty(userKey)) return; // can't locate an anonymous row by identity

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE configs
                   SET token_json = $j, updated_at = $u
                 WHERE service = $s AND user_key = $k
                """;
            cmd.Parameters.AddWithValue("$j", SerializeObject(tokenData));
            cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$s", (int)tokenData.anime_service);
            cmd.Parameters.AddWithValue("$k", userKey);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Identity column for the unique index. user_id for AniList (extracted from the JWT),
        /// username for Kitsu (the credentials the user typed). Anonymous installs have neither.
        /// </summary>
        private static string GetUserKey(TokenData td) => td.anime_service switch
        {
            AnimeService.Anilist => td.user_id,
            AnimeService.Kitsu => td.username,
            _ => null,
        };

        /// <summary>
        /// 16 random bytes encoded as URL-safe base64 (22 chars, no padding).
        /// 128 bits of entropy is overkill for collision avoidance and plenty for use as an
        /// unguessable bearer token — anyone with the UID can use the install, same as today.
        /// </summary>
        private static string GenerateUid()
        {
            Span<byte> buf = stackalloc byte[16];
            RandomNumberGenerator.Fill(buf);
            return Base64UrlEncode(buf.ToArray());
        }
    }
}
