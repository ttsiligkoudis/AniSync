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
            _connectionString = SqliteConnectionFactory.BuildConnectionString(configuration);

            EnsureSchema();
        }

        private void EnsureSchema()
        {
            using var conn = SqliteConnectionFactory.OpenConnection(_connectionString);

            // Single canonical schema — pre-launch, so we don't carry an ALTER ladder for
            // existing DBs. If a dev has an older anisync.db lying around they should
            // delete it. Identity is denormalised into one column per provider so the
            // login-flow lookup ("which row owns this AniList/MAL/Kitsu identity?") is a
            // single indexed B-tree probe regardless of whether the identity is the row's
            // primary or one of its linked secondaries — replaces the json_each scan that
            // the earlier two-lookup design needed.
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS configs (
                    uid               TEXT PRIMARY KEY,
                    service           INTEGER NOT NULL,                     -- which provider is the primary
                    token_json        TEXT NOT NULL,                        -- primary's TokenData JSON
                    linked_tokens     TEXT,                                 -- non-primary TokenDatas as JSON array

                    -- One slot per provider. Holds the same identifier GetUserKey extracts
                    -- (user_id for AniList/MAL, username for Kitsu) regardless of whether
                    -- the slot is the row's primary or a linked secondary. Each gets its
                    -- own unique partial index so login-flow lookups are O(log n).
                    anilist_user_key  TEXT,
                    kitsu_user_key    TEXT,
                    mal_user_key      TEXT,

                    flags             INTEGER NOT NULL DEFAULT 0,
                    revision          INTEGER NOT NULL DEFAULT 0,
                    scrobble_token    TEXT,
                    plex_username     TEXT,
                    -- JSON array of { url, name } pairs — the user's
                    -- configured Stremio stream addons (Torrentio /
                    -- MediaFusion / Comet / Jackettio / AIOStreams /
                    -- …). Episode lookups fan out across every entry.
                    -- Replaces the v1 real_debrid_api_key +
                    -- mediafusion_manifest_url columns — addon-specific
                    -- config (keys, encrypted blobs, indexer toggles)
                    -- lives inside each manifest URL now.
                    stream_addons     TEXT,
                    created_at        INTEGER NOT NULL,
                    updated_at        INTEGER NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_configs_anilist
                    ON configs(anilist_user_key) WHERE anilist_user_key IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS idx_configs_kitsu
                    ON configs(kitsu_user_key)   WHERE kitsu_user_key   IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS idx_configs_mal
                    ON configs(mal_user_key)     WHERE mal_user_key     IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS idx_configs_scrobble_token
                    ON configs(scrobble_token)   WHERE scrobble_token   IS NOT NULL;

                -- Per-user episode notifications. Rendered in the site-header bell
                -- with an unread badge; click of a row deep-links into the watch
                -- page. The unique index on (uid, anime_id, season, episode_number)
                -- below is the backstop dedup; primary dedup lives in
                -- NotificationStore.CreateAsync, which also collapses cross-
                -- service-prefix duplicates (anilist:21 / mal:21 / kitsu:11061
                -- = same physical anime) so a primary-provider flip mid-cron-
                -- window doesn't produce two bell rows for the same episode.
                CREATE TABLE IF NOT EXISTS notifications (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    uid             TEXT NOT NULL,
                    service         INTEGER NOT NULL,        -- AnimeService enum int, pinned at create-time
                    anime_id        TEXT NOT NULL,           -- prefixed id, e.g. anilist:123
                    anime_title     TEXT NOT NULL,
                    episode_number  INTEGER NOT NULL,
                    season          INTEGER,                 -- nullable; routes collapse null → season 1
                    thumbnail_url   TEXT,
                    link_path       TEXT NOT NULL,           -- pre-baked /anime/{id}/watch/{ep} deep link
                    created_at      INTEGER NOT NULL,
                    read_at         INTEGER                  -- NULL = unread
                );
                CREATE INDEX IF NOT EXISTS idx_notifications_uid_created
                    ON notifications(uid, created_at DESC);
                -- COALESCE so a NULL season is a single value rather than "every row distinct"
                -- (SQLite's default), which would break the dedup.
                CREATE UNIQUE INDEX IF NOT EXISTS idx_notifications_unique
                    ON notifications(uid, anime_id, COALESCE(season, 0), episode_number);
                -- Partial index keeps the unread-count query tiny — the bell polls this
                -- once a minute per logged-in tab so the read path matters.
                CREATE INDEX IF NOT EXISTS idx_notifications_unread
                    ON notifications(uid) WHERE read_at IS NULL;

                -- Per-user snapshot of "Watching"-status anime ids. The episode
                -- notification dispatcher reads this to decide which airing
                -- episodes match which users without hammering AniList/MAL/Kitsu
                -- every 5 minutes. Refreshed lazily (6h staleness gate) when the
                -- cron tick runs.
                CREATE TABLE IF NOT EXISTS user_watching_cache (
                    uid             TEXT PRIMARY KEY,
                    service         INTEGER NOT NULL,        -- user's primary AnimeService at cache time
                    media_ids_json  TEXT NOT NULL,           -- JSON array of prefixed ids
                    refreshed_at    INTEGER NOT NULL,        -- 0 means "explicitly invalidated, refresh on next read"
                    last_error      TEXT,
                    last_error_at   INTEGER
                );
                CREATE INDEX IF NOT EXISTS idx_user_watching_cache_stale
                    ON user_watching_cache(refreshed_at);

                -- Per-browser Web Push subscriptions. One row per (uid,
                -- endpoint) pair — a user with multiple devices /
                -- browsers gets multiple rows, all of them notified
                -- on dispatch. UNIQUE (uid, endpoint) so a repeated
                -- subscribe from the same browser updates the existing
                -- row instead of stacking duplicates.
                CREATE TABLE IF NOT EXISTS push_subscriptions (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    uid         TEXT NOT NULL,
                    endpoint    TEXT NOT NULL,
                    p256dh      TEXT NOT NULL,
                    auth        TEXT NOT NULL,
                    user_agent  TEXT,
                    created_at  INTEGER NOT NULL,
                    UNIQUE (uid, endpoint)
                );
                CREATE INDEX IF NOT EXISTS idx_push_subscriptions_uid
                    ON push_subscriptions(uid);

                -- Per-(uid, episode) stream-addon fan-out cache used to
                -- live here; it's been moved into the browser's
                -- localStorage so the entries stay close to the user
                -- whose IP-bound debrid URLs were signed for it. The
                -- DROP keeps disk usage honest on existing installs
                -- where the table was created by an earlier deploy.
                DROP TABLE IF EXISTS user_stream_cache;
                """;
            create.ExecuteNonQuery();

            // Idempotent column additions for deployed databases predating
            // the column. Stays even though the canonical CREATE above
            // already lists the column — pre-existing fly.dev DB files
            // were minted before that column was added and SQLite's
            // CREATE IF NOT EXISTS doesn't alter existing tables. The
            // try/catch covers the "already exists" race on a fresh DB.
            EnsureColumn(conn, "configs", "stream_addons", "TEXT");
        }

        private static void EnsureColumn(SqliteConnection conn, string table, string column, string typeAndConstraints)
        {
            using var check = conn.CreateCommand();
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk.
            // Name is at ordinal 1.
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return; // already present
                }
            }
            reader.Close();

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAndConstraints}";
            alter.ExecuteNonQuery();
        }

        public async Task<string> UpsertAsync(TokenData tokenData)
        {
            var serviceInt = (int)tokenData.anime_service;
            var userKey = GetUserKey(tokenData);
            var identityCol = IdentityColumnFor(tokenData.anime_service);
            var json = SerializeObject(tokenData);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);

            var newUid = GenerateUid();
            using var cmd = conn.CreateCommand();
            if (string.IsNullOrEmpty(userKey) || identityCol == null)
            {
                // Anonymous rows leave all three identity columns NULL — nothing to dedup on,
                // and the random 128-bit uid can't collide, so a plain INSERT never conflicts.
                cmd.CommandText = """
                    INSERT INTO configs (uid, service, token_json, created_at, updated_at, flags)
                    VALUES ($uid, $s, $j, $c, $u, $f)
                    RETURNING uid
                    """;
            }
            else
            {
                // Single atomic upsert keyed on the per-service identity column. The old
                // read-(UPDATE)-then-INSERT design raced: two concurrent logins for the same
                // identity (double-clicked "Allow", or an OAuth callback landing alongside a
                // session-rehydration write) both found no row and both INSERTed, and the
                // loser tripped the UNIQUE partial index and surfaced a 500. Folding it into
                // one INSERT … ON CONFLICT makes the loser refresh the winner's row instead.
                //
                // The DO UPDATE WHERE keeps the old `service = $s` gate: we only refresh a row
                // that owns this identity as its *primary*. If the identity is a linked
                // secondary on a different row (service differs) the update no-ops — callers
                // are meant to route those through FindUidByIdentityAsync + the linked-merge
                // path, so reaching here is a rare race, and a no-op is far safer than letting
                // a bare DO UPDATE overwrite that row's primary token. The conflict-target
                // WHERE textually matches the partial index so SQLite accepts it as the target.
                cmd.CommandText = $"""
                    INSERT INTO configs (uid, service, {identityCol}, token_json, created_at, updated_at, flags)
                    VALUES ($uid, $s, $k, $j, $c, $u, $f)
                    ON CONFLICT({identityCol}) WHERE {identityCol} IS NOT NULL
                    DO UPDATE SET token_json = excluded.token_json, updated_at = excluded.updated_at
                          WHERE configs.service = excluded.service
                    RETURNING uid
                    """;
                cmd.Parameters.AddWithValue("$k", userKey);
            }
            cmd.Parameters.AddWithValue("$uid", newUid);
            cmd.Parameters.AddWithValue("$s", serviceInt);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$c", now);
            cmd.Parameters.AddWithValue("$u", now);
            cmd.Parameters.AddWithValue("$f", DefaultFlagsPacked);

            // RETURNING yields newUid on a fresh INSERT and the existing uid when the conflict
            // refreshed a primary row. It yields nothing only when the conflicting row owns the
            // identity as a *linked* secondary (DO UPDATE WHERE was false) — the rare race noted
            // above, where no row was written and null is the honest answer.
            return await cmd.ExecuteScalarAsync() as string;
        }

        // Default packed flags for a freshly-linked account. flags1 byte enables
        // Currently Watching (0x01), Seasonal Anime (0x08), and the Discover-Only
        // bit for Seasonal (0x80). flags2 and flags3 stay zero — every other
        // catalog / stream toggle defaults off until the user explicitly turns it
        // on in the configure page. Encoded as the same packed integer
        // SetFlagsAsync writes: bits 16-23 = flags1, 8-15 = flags2, 0-7 = flags3.
        private const long DefaultFlagsPacked = ((long)(0x01 | 0x08 | 0x80)) << 16;

        public async Task<(string uid, bool isPrimaryMatch)> FindUidByIdentityAsync(TokenData candidate)
        {
            if (candidate == null) return (null, false);
            var userKey = GetUserKey(candidate);
            var identityCol = IdentityColumnFor(candidate.anime_service);
            if (string.IsNullOrEmpty(userKey) || identityCol == null) return (null, false);

            // Single indexed lookup against the candidate's per-service identity column.
            // The column is populated for both primary and linked slots, so one B-tree
            // probe finds either flavour of match. The row's `service` column tells us
            // which flavour it is — equal to candidate.anime_service means the slot we
            // matched is the row's primary; otherwise the candidate is currently a
            // linked secondary on this row and the caller should route through the
            // linked-merge path instead of the refresh-primary path.
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT uid, service FROM configs WHERE {identityCol} = $k LIMIT 1";
            cmd.Parameters.AddWithValue("$k", userKey);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return (null, false);
            var uid = reader.GetString(0);
            var primaryService = (AnimeService)reader.GetInt32(1);
            return (uid, primaryService == candidate.anime_service);
        }

        public async Task<TokenData> GetAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);

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
            var identityCol = IdentityColumnFor(tokenData.anime_service);
            if (string.IsNullOrEmpty(userKey) || identityCol == null) return; // anonymous: can't locate by identity

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);

            // Primary-only update — gated on `service = $s` so a token refresh for an
            // identity that's currently a *linked* secondary doesn't accidentally
            // overwrite some other user's primary. The linked-token-refresh path goes
            // through SetLinkedTokenAsync, which takes a uid argument explicitly.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE configs
                   SET token_json = $j, updated_at = $u
                 WHERE service = $s AND {identityCol} = $k
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

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
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

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
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

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
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

        public async Task<string> EnsureScrobbleTokenAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);

            using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT scrobble_token FROM configs WHERE uid = $uid LIMIT 1";
                read.Parameters.AddWithValue("$uid", uid);
                using var reader = await read.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return null;
                if (!reader.IsDBNull(0))
                {
                    var existing = reader.GetString(0);
                    if (!string.IsNullOrEmpty(existing)) return existing;
                }
            }

            // First call: generate, store, return. Loop to defend against the (vanishingly
            // unlikely) collision with another concurrently-generated token.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var token = GenerateUid();
                try
                {
                    using var write = conn.CreateCommand();
                    write.CommandText = """
                        UPDATE configs
                           SET scrobble_token = $t, updated_at = $u
                         WHERE uid = $uid AND scrobble_token IS NULL
                        """;
                    write.Parameters.AddWithValue("$t", token);
                    write.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    write.Parameters.AddWithValue("$uid", uid);
                    var affected = await write.ExecuteNonQueryAsync();
                    if (affected == 0)
                    {
                        // Another caller stored one between our SELECT and UPDATE — read theirs.
                        using var reread = conn.CreateCommand();
                        reread.CommandText = "SELECT scrobble_token FROM configs WHERE uid = $uid LIMIT 1";
                        reread.Parameters.AddWithValue("$uid", uid);
                        return await reread.ExecuteScalarAsync() as string;
                    }
                    return token;
                }
                catch (SqliteException) { /* unique-index collision — try a fresh token */ }
            }
            return null;
        }

        public async Task<string> RotateScrobbleTokenAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var token = GenerateUid();
                try
                {
                    using var write = conn.CreateCommand();
                    write.CommandText = """
                        UPDATE configs
                           SET scrobble_token = $t, updated_at = $u
                         WHERE uid = $uid
                        """;
                    write.Parameters.AddWithValue("$t", token);
                    write.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    write.Parameters.AddWithValue("$uid", uid);
                    if (await write.ExecuteNonQueryAsync() == 0) return null; // unknown uid
                    return token;
                }
                catch (SqliteException) { /* collision — retry */ }
            }
            return null;
        }

        public async Task<string> ResolveUidByScrobbleTokenAsync(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT uid FROM configs WHERE scrobble_token = $t LIMIT 1";
            cmd.Parameters.AddWithValue("$t", token);
            return await cmd.ExecuteScalarAsync() as string;
        }

        public async Task SetPlexUsernameAsync(string uid, string username)
        {
            if (string.IsNullOrEmpty(uid)) return;

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE configs
                   SET plex_username = $u, updated_at = $ts
                 WHERE uid = $uid
                """;
            cmd.Parameters.AddWithValue("$u",
                string.IsNullOrWhiteSpace(username) ? (object)DBNull.Value : username.Trim());
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$uid", uid);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<string> GetPlexUsernameAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT plex_username FROM configs WHERE uid = $uid LIMIT 1";
            cmd.Parameters.AddWithValue("$uid", uid);
            return await cmd.ExecuteScalarAsync() as string;
        }

        public async Task<List<StreamAddon>> GetStreamAddonsAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return new List<StreamAddon>();

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT stream_addons FROM configs WHERE uid = $uid LIMIT 1";
            cmd.Parameters.AddWithValue("$uid", uid);
            var json = await cmd.ExecuteScalarAsync() as string;
            return DeserializeStreamAddons(json);
        }

        public async Task<bool> AddStreamAddonAsync(string uid, StreamAddon addon)
        {
            if (string.IsNullOrEmpty(uid) || addon == null || string.IsNullOrEmpty(addon.Url))
                return false;

            // Read-modify-write — addon add/remove is user-driven (one
            // click per addon on the Configure page), so the
            // concurrency surface is negligible. Skip if the URL is
            // already in the list (idempotent) to spare the user
            // having to deduplicate by hand.
            var existing = await GetStreamAddonsAsync(uid);
            if (existing.Any(a => string.Equals(a.Url, addon.Url, StringComparison.OrdinalIgnoreCase)))
                return false;

            existing.Add(addon);
            await WriteStreamAddonsAsync(uid, existing);
            return true;
        }

        public async Task<bool> RemoveStreamAddonAsync(string uid, string addonUrl)
        {
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(addonUrl)) return false;

            var existing = await GetStreamAddonsAsync(uid);
            var removed = existing.RemoveAll(
                a => string.Equals(a.Url, addonUrl, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;

            await WriteStreamAddonsAsync(uid, existing);
            return true;
        }

        public async Task<bool> ReorderStreamAddonsAsync(string uid, IList<string> orderedUrls)
        {
            if (string.IsNullOrEmpty(uid) || orderedUrls == null) return false;
            var existing = await GetStreamAddonsAsync(uid);
            if (existing.Count == 0) return false;

            // Build a URL → addon lookup. Drain it as we walk the
            // ordered list so anything leftover at the end (an addon
            // the client didn't mention) survives — defensive against
            // a stale client losing rows. Comparer-insensitive match
            // mirrors the duplicate-detection in AddStreamAddonAsync.
            var byUrl = existing.ToDictionary(a => a.Url, a => a, StringComparer.OrdinalIgnoreCase);
            var reordered = new List<StreamAddon>(existing.Count);
            foreach (var url in orderedUrls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                if (byUrl.Remove(url, out var addon)) reordered.Add(addon);
            }
            reordered.AddRange(byUrl.Values);

            // No-op when the order didn't change — avoids a needless
            // sqlite write + the cache invalidation that comes with it.
            var changed = false;
            for (int i = 0; i < existing.Count; i++)
            {
                if (!string.Equals(existing[i].Url, reordered[i].Url, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    break;
                }
            }
            if (!changed) return false;

            await WriteStreamAddonsAsync(uid, reordered);
            return true;
        }

        private async Task WriteStreamAddonsAsync(string uid, List<StreamAddon> addons)
        {
            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE configs
                   SET stream_addons = $j, updated_at = $ts
                 WHERE uid = $uid
                """;
            // NULL out the column when the list is empty so we don't
            // keep "[]" around as dead bytes forever.
            cmd.Parameters.AddWithValue("$j",
                addons.Count == 0 ? (object)DBNull.Value : SerializeObject(addons));
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$uid", uid);
            await cmd.ExecuteNonQueryAsync();
        }

        private static List<StreamAddon> DeserializeStreamAddons(string json)
        {
            if (string.IsNullOrEmpty(json)) return new List<StreamAddon>();
            try { return DeserializeObject<List<StreamAddon>>(json) ?? new List<StreamAddon>(); }
            catch { return new List<StreamAddon>(); }
        }

        public async Task DeleteAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM configs WHERE uid = $uid";
            cmd.Parameters.AddWithValue("$uid", uid);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<string> RotateUidAsync(string oldUid)
        {
            if (string.IsNullOrEmpty(oldUid)) return null;

            var newUid = GenerateUid();

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

            // Repoint the primary row first. Zero rows updated means the UID is unknown —
            // roll back and report the miss so the caller doesn't hand out a dangling UID.
            using (var cfg = conn.CreateCommand())
            {
                cfg.Transaction = tx;
                cfg.CommandText = "UPDATE configs SET uid = $new WHERE uid = $old";
                cfg.Parameters.AddWithValue("$new", newUid);
                cfg.Parameters.AddWithValue("$old", oldUid);
                if (await cfg.ExecuteNonQueryAsync() == 0)
                {
                    await tx.RollbackAsync();
                    return null;
                }
            }

            // Cascade to the uid-keyed satellite tables so the user keeps their existing
            // notifications, watching snapshot, and push subscriptions under the new UID.
            foreach (var table in new[] { "notifications", "user_watching_cache", "push_subscriptions" })
            {
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = $"UPDATE {table} SET uid = $new WHERE uid = $old";
                upd.Parameters.AddWithValue("$new", newUid);
                upd.Parameters.AddWithValue("$old", oldUid);
                await upd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return newUid;
        }

        public async Task<List<LinkedToken>> GetLinkedTokensAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return [];

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            return await ReadLinkedTokensAsync(conn, null, uid);
        }

        private static async Task<List<LinkedToken>> ReadLinkedTokensAsync(SqliteConnection conn, SqliteTransaction tx, string uid)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT linked_tokens FROM configs WHERE uid = $uid LIMIT 1";
            cmd.Parameters.AddWithValue("$uid", uid);
            var json = await cmd.ExecuteScalarAsync() as string;
            return DeserializeLinkedTokens(json);
        }

        public async Task SetLinkedTokenAsync(string uid, LinkedToken linked)
        {
            if (string.IsNullOrEmpty(uid) || linked == null) return;

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            // BEGIN IMMEDIATE: grab the write lock up front so this read-modify-write of the
            // linked_tokens array is serialised against a concurrent link / unlink / login-time
            // linked-token refresh for the same uid. A deferred txn would let two callers both
            // read the old array and the second commit would clobber the first's entry.
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(deferred: false);

            var existing = await ReadLinkedTokensAsync(conn, tx, uid);
            var idx = existing.FindIndex(t => t.Service == linked.Service);
            if (idx >= 0) existing[idx] = linked;
            else existing.Add(linked);

            // The linked entry's identity goes into its own per-service column too so the
            // login-flow lookup picks it up via FindUidByIdentityAsync's indexed probe
            // without parsing the JSON. NULL-safe: if the linked TokenData has no usable
            // identity (shouldn't happen in practice, every provider returns one), the
            // column stays at whatever it was — JSON is the source of truth and the index
            // is a denormalised helper.
            await WriteLinkedTokensAsync(conn, tx, uid, existing, linked.Service, GetUserKey(linked.TokenData));
            await tx.CommitAsync();
        }

        public async Task RemoveLinkedTokenAsync(string uid, AnimeService service)
        {
            if (string.IsNullOrEmpty(uid)) return;

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);
            using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(deferred: false);

            var existing = await ReadLinkedTokensAsync(conn, tx, uid);
            var removed = existing.RemoveAll(t => t.Service == service);
            if (removed == 0)
            {
                await tx.RollbackAsync();
                return;
            }

            // Clear the per-service identity column alongside the JSON rewrite so the
            // user can immediately re-link the same provider with a different account
            // without tripping the unique partial index.
            await WriteLinkedTokensAsync(conn, tx, uid, existing, service, null);
            await tx.CommitAsync();
        }

        public async Task<(TokenData newPrimary, string reason)> SwapPrimaryAsync(string uid, AnimeService newPrimaryService, bool resolveCollision = false)
        {
            if (string.IsNullOrEmpty(uid)) return (null, "uid-missing");

            using var conn = await SqliteConnectionFactory.OpenConnectionAsync(_connectionString);

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
            var newIdentityCol = IdentityColumnFor(newPrimary.anime_service);
            var newPrimaryJson = SerializeObject(newPrimary);
            var newLinkedJson = linked.Count == 0 ? (object)DBNull.Value : SerializeObject(linked);

            // The per-service identity columns don't change in a swap — both the old and
            // new primaries' identities are still attached to this row, just with the
            // primary slot pointing to a different one. So the UPDATE only rewrites
            // `service`, `token_json`, and `linked_tokens`. No identity-column writes
            // means no risk of tripping the unique partial index from the swap itself;
            // the only collision surface is on a *different* row that already owns the
            // same identity in its own slot, which is the case the catch below handles.
            async Task<int> RunUpdate()
            {
                using var update = conn.CreateCommand();
                update.CommandText = """
                    UPDATE configs
                       SET service = $s, token_json = $j,
                           linked_tokens = $l, updated_at = $u
                     WHERE uid = $uid
                    """;
                update.Parameters.AddWithValue("$s", newServiceInt);
                update.Parameters.AddWithValue("$j", newPrimaryJson);
                update.Parameters.AddWithValue("$l", newLinkedJson);
                update.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                update.Parameters.AddWithValue("$uid", uid);
                return await update.ExecuteNonQueryAsync();
            }

            // Collision check still happens — but pre-flight rather than via SqliteException,
            // because the UPDATE itself doesn't touch any UNIQUE column anymore. A different
            // row owning the new primary's identity (in any slot — primary OR linked) means
            // promoting here would orphan that other install's view of the same account.
            async Task<bool> NewPrimaryClashesElsewhereAsync()
            {
                if (string.IsNullOrEmpty(newUserKey) || newIdentityCol == null) return false;
                using var probe = conn.CreateCommand();
                probe.CommandText = $"SELECT 1 FROM configs WHERE {newIdentityCol} = $k AND uid <> $uid LIMIT 1";
                probe.Parameters.AddWithValue("$k", newUserKey);
                probe.Parameters.AddWithValue("$uid", uid);
                return await probe.ExecuteScalarAsync() != null;
            }

            if (await NewPrimaryClashesElsewhereAsync())
            {
                if (!resolveCollision) return (null, "collision");
                // Force path: delete the colliding row first, then proceed with the swap.
                // The colliding row keeps its own UID, flags, and linked_tokens — caller
                // must have warned the user that other-install state is lost.
                using var del = conn.CreateCommand();
                del.CommandText = $"DELETE FROM configs WHERE {newIdentityCol} = $k AND uid <> $uid";
                del.Parameters.AddWithValue("$k", newUserKey);
                del.Parameters.AddWithValue("$uid", uid);
                await del.ExecuteNonQueryAsync();
            }

            await RunUpdate();
            return (newPrimary, null);
        }

        private static async Task WriteLinkedTokensAsync(SqliteConnection conn, SqliteTransaction tx, string uid, List<LinkedToken> tokens, AnimeService changedService, string changedUserKey)
        {
            var identityCol = IdentityColumnFor(changedService);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // One UPDATE writes both the JSON array and the affected per-service identity
            // column so the index never disagrees with the JSON. NULL'ing the column when
            // changedUserKey is null doubles as the unlink path. If the service didn't
            // map to a column (anonymous / unknown enum) we just skip the column write.
            if (identityCol == null)
            {
                cmd.CommandText = """
                    UPDATE configs
                       SET linked_tokens = $j, updated_at = $u
                     WHERE uid = $uid
                    """;
            }
            else
            {
                cmd.CommandText = $"""
                    UPDATE configs
                       SET linked_tokens = $j, {identityCol} = $k, updated_at = $u
                     WHERE uid = $uid
                    """;
                cmd.Parameters.AddWithValue("$k", (object)changedUserKey ?? DBNull.Value);
            }
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
        /// Identity value for the unique index. user_id for AniList (from the JWT) and MAL
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
        /// Identity column name for the given service. Closed enum, so the result is a
        /// known constant — safe to interpolate into SQL. Returns null for unknown enum
        /// values (defensive — every defined service maps to a column).
        /// </summary>
        private static string IdentityColumnFor(AnimeService service) => service switch
        {
            AnimeService.Anilist     => "anilist_user_key",
            AnimeService.MyAnimeList => "mal_user_key",
            AnimeService.Kitsu       => "kitsu_user_key",
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
