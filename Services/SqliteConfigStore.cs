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

            using var create = conn.CreateCommand();
            create.CommandText = """
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
            create.ExecuteNonQuery();

            // Idempotent migration: add `flags` column on existing DBs that pre-date the
            // "configuration in DB" change. Stores the three flag bytes packed into a single
            // INTEGER (bits 16-23 = flags1, 8-15 = flags2, 0-7 = flags3).
            if (!ColumnExists(conn, "configs", "flags"))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE configs ADD COLUMN flags INTEGER NOT NULL DEFAULT 0";
                alter.ExecuteNonQuery();
            }

            // Idempotent migration: add `revision` column. The revision counter bumps on every
            // SetFlagsAsync call and is appended to the install URL so Stremio's cache treats
            // each save as a new addon URL — Stremio doesn't refetch the manifest for an
            // already-installed URL even after force-restart, so we have to make the URL
            // visibly different to force a refresh.
            if (!ColumnExists(conn, "configs", "revision"))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE configs ADD COLUMN revision INTEGER NOT NULL DEFAULT 0";
                alter.ExecuteNonQuery();
            }

            // Idempotent migration: add `linked_tokens` for the multi-provider sync feature.
            // Holds a JSON array of LinkedToken objects so writes (Manage Entry save/delete,
            // auto-track) can fan out from the primary provider to additional linked accounts.
            // Stored as JSON rather than a sibling table because there are at most two rows
            // per user (the other two providers) and no query patterns benefit from a join.
            if (!ColumnExists(conn, "configs", "linked_tokens"))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE configs ADD COLUMN linked_tokens TEXT";
                alter.ExecuteNonQuery();
            }
        }

        private static bool ColumnExists(SqliteConnection conn, string table, string column)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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

        public async Task<(byte flags1, byte flags2, byte flags3, long revision)> GetFlagsAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return (0, 0, 0, 0);

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT flags, revision FROM configs WHERE uid = $uid LIMIT 1";
            cmd.Parameters.AddWithValue("$uid", uid);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return (0, 0, 0, 0);

            var packed = reader.GetInt64(0);
            var revision = reader.GetInt64(1);
            return (
                (byte)((packed >> 16) & 0xff),
                (byte)((packed >> 8) & 0xff),
                (byte)(packed & 0xff),
                revision);
        }

        public async Task<long> GetRevisionAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return 0;

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT revision FROM configs WHERE uid = $uid LIMIT 1";
            cmd.Parameters.AddWithValue("$uid", uid);
            var raw = await cmd.ExecuteScalarAsync();
            return raw is long l ? l : 0L;
        }

        public async Task<long> SetFlagsAsync(string uid, byte flags1, byte flags2, byte flags3)
        {
            if (string.IsNullOrEmpty(uid)) return 0;
            var packed = ((long)flags1 << 16) | ((long)flags2 << 8) | flags3;

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Bump revision atomically with the flag write so the new install URL is
            // guaranteed to differ from the URL Stremio currently has cached.
            cmd.CommandText = """
                UPDATE configs
                   SET flags = $f, revision = revision + 1, updated_at = $u
                 WHERE uid = $uid
                RETURNING revision
                """;
            cmd.Parameters.AddWithValue("$f", packed);
            cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$uid", uid);
            var raw = await cmd.ExecuteScalarAsync();
            return raw is long l ? l : 0L;
        }

        public async Task DeleteAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM configs WHERE uid = $uid";
            cmd.Parameters.AddWithValue("$uid", uid);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task DeleteByUserAsync(TokenData tokenData)
        {
            if (tokenData == null) return;
            var userKey = GetUserKey(tokenData);
            if (string.IsNullOrEmpty(userKey)) return;

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM configs WHERE service = $s AND user_key = $k";
            cmd.Parameters.AddWithValue("$s", (int)tokenData.anime_service);
            cmd.Parameters.AddWithValue("$k", userKey);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<LinkedToken>> GetLinkedTokensAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return [];

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT linked_tokens FROM configs WHERE uid = $uid LIMIT 1";
            cmd.Parameters.AddWithValue("$uid", uid);
            var json = await cmd.ExecuteScalarAsync() as string;
            return DeserializeLinkedTokens(json);
        }

        public async Task SetLinkedTokenAsync(string uid, LinkedToken linked)
        {
            if (string.IsNullOrEmpty(uid) || linked == null) return;

            // Read-modify-write — link/unlink is rare so we don't bother with row-level locking.
            // Concurrent links for the same UID could lose one update, but the linking flow is
            // user-driven (one click per provider) so the chance of collision is negligible.
            var existing = await GetLinkedTokensAsync(uid);
            var idx = existing.FindIndex(t => t.Service == linked.Service);
            if (idx >= 0) existing[idx] = linked;
            else existing.Add(linked);

            await WriteLinkedTokensAsync(uid, existing);
        }

        public async Task RemoveLinkedTokenAsync(string uid, AnimeService service)
        {
            if (string.IsNullOrEmpty(uid)) return;

            var existing = await GetLinkedTokensAsync(uid);
            var removed = existing.RemoveAll(t => t.Service == service);
            if (removed == 0) return;

            await WriteLinkedTokensAsync(uid, existing);
        }

        public async Task<(TokenData newPrimary, string reason)> SwapPrimaryAsync(string uid, AnimeService newPrimaryService)
        {
            if (string.IsNullOrEmpty(uid)) return (null, "uid-missing");

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            // Read the current row so we can reshuffle primary <-> linked atomically below.
            string oldPrimaryJson = null;
            string linkedJson = null;
            using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT token_json, linked_tokens FROM configs WHERE uid = $uid LIMIT 1";
                read.Parameters.AddWithValue("$uid", uid);
                using var reader = await read.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return (null, "no-primary");
                oldPrimaryJson = reader.GetString(0);
                linkedJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            var oldPrimary = DeserializeObject<TokenData>(oldPrimaryJson);
            if (oldPrimary == null) return (null, "no-primary");

            var linked = DeserializeLinkedTokens(linkedJson);
            var idx = linked.FindIndex(l => l.Service == newPrimaryService);
            if (idx < 0) return (null, "not-linked");

            var chosen = linked[idx];
            // Refuse to promote a broken link — would just leave the user effectively logged
            // out and trigger another re-auth round-trip immediately.
            if (chosen.NeedsReauth) return (null, "needs-reauth");
            if (chosen.TokenData == null) return (null, "no-token");

            // Build the new linked list: the chosen one moves out, the old primary moves in.
            // We don't carry NeedsReauth across because we only got here on a healthy primary.
            linked.RemoveAt(idx);
            linked.Add(new LinkedToken
            {
                Service = oldPrimary.anime_service,
                TokenData = oldPrimary,
                NeedsReauth = false,
            });

            var newPrimary = chosen.TokenData;
            var newServiceInt = (int)newPrimary.anime_service;
            var newUserKey = GetUserKey(newPrimary);
            var newPrimaryJson = SerializeObject(newPrimary);
            var newLinkedJson = linked.Count == 0 ? (object)DBNull.Value : SerializeObject(linked);

            using var update = conn.CreateCommand();
            update.CommandText = """
                UPDATE configs
                   SET service = $s, user_key = $k, token_json = $j,
                       linked_tokens = $l, updated_at = $u
                 WHERE uid = $uid
                """;
            update.Parameters.AddWithValue("$s", newServiceInt);
            update.Parameters.AddWithValue("$k", (object)newUserKey ?? DBNull.Value);
            update.Parameters.AddWithValue("$j", newPrimaryJson);
            update.Parameters.AddWithValue("$l", newLinkedJson);
            update.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            update.Parameters.AddWithValue("$uid", uid);

            try
            {
                await update.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // Unique (service, user_key) collision — another configs row already owns
                // this identity, e.g. the user has a separate install elsewhere with the
                // linked account as primary. The caller surfaces a friendly error.
                return (null, "collision");
            }

            return (newPrimary, null);
        }

        private async Task WriteLinkedTokensAsync(string uid, List<LinkedToken> tokens)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE configs
                   SET linked_tokens = $j, updated_at = $u
                 WHERE uid = $uid
                """;
            // Drop the column to NULL when the list is empty so we don't keep "[]" around
            // forever after the user unlinks their last provider.
            cmd.Parameters.AddWithValue("$j", tokens.Count == 0 ? (object)DBNull.Value : SerializeObject(tokens));
            cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$uid", uid);
            await cmd.ExecuteNonQueryAsync();
        }

        private static List<LinkedToken> DeserializeLinkedTokens(string json)
        {
            if (string.IsNullOrEmpty(json)) return [];
            try { return DeserializeObject<List<LinkedToken>>(json) ?? []; }
            catch { return []; }
        }

        /// <summary>
        /// Identity column for the unique index. user_id for AniList (from the JWT) and MAL
        /// (from /users/@me), username for Kitsu (the credentials the user typed). Anonymous
        /// installs have neither.
        /// </summary>
        private static string GetUserKey(TokenData td) => td.anime_service switch
        {
            AnimeService.Anilist => td.user_id,
            AnimeService.MyAnimeList => td.user_id,
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
