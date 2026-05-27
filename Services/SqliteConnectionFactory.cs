using Microsoft.Data.Sqlite;

namespace AnimeList.Services
{
    /// <summary>
    /// Shared connection-string builder for the SQLite stores that all sit
    /// on the same <c>anisync.db</c> file (<see cref="SqliteConfigStore"/>,
    /// <see cref="NotificationStore"/>, <see cref="WatchingCacheStore"/>).
    /// Centralises the <c>ANISYNC_DATA_DIR</c> resolution and the WAL /
    /// busy_timeout open sequence so a new store doesn't have to replicate
    /// the fallback + pooling + concurrency settings.
    /// </summary>
    public static class SqliteConnectionFactory
    {
        /// <summary>
        /// Builds the canonical connection string against <c>anisync.db</c>
        /// in the configured data directory. Creates the directory if it
        /// doesn't exist so callers don't have to. Idempotent.
        /// </summary>
        public static string BuildConnectionString(IConfiguration configuration)
        {
            // Honour ANISYNC_DATA_DIR (Fly.io sets it to /data) and fall back to the
            // working directory, which is enough for local dev. Make sure the
            // directory exists so the SQLite open call doesn't fail with "unable
            // to open database file".
            var dataDir = configuration["ANISYNC_DATA_DIR"]
                ?? Environment.GetEnvironmentVariable("ANISYNC_DATA_DIR")
                ?? ".";
            Directory.CreateDirectory(dataDir);

            return new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDir, "anisync.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                // Private cache (one page-cache per connection), not shared. WAL gives
                // readers + a single writer real concurrency, and busy_timeout (set in
                // OpenConnectionAsync) only waits out the file-level SQLITE_BUSY that
                // private cache produces. Shared cache would instead serialise every
                // connection on in-process table locks and surface SQLITE_LOCKED, which
                // the busy handler can't wait on — defeating the timeout entirely.
                Cache = SqliteCacheMode.Private,
            }.ToString();
        }

        // Run on every open. busy_timeout makes a writer that finds the db locked wait
        // up to 5s for the lock instead of failing immediately ("database is locked").
        // journal_mode=WAL is persistent (stored in the db header) so it only does work
        // the first time, but is cheap to re-assert. synchronous=NORMAL is per-connection
        // and safe under WAL — a crash can lose the last transaction but never corrupts.
        private const string PragmaSql =
            "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";

        /// <summary>
        /// Opens a connection and applies the concurrency PRAGMAs. Use everywhere a store
        /// needs a connection so no write path skips the busy_timeout / WAL setup.
        /// </summary>
        public static async Task<SqliteConnection> OpenConnectionAsync(string connectionString)
        {
            var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = PragmaSql;
            await cmd.ExecuteNonQueryAsync();
            return conn;
        }

        /// <summary>
        /// Synchronous counterpart to <see cref="OpenConnectionAsync"/> for the one-shot
        /// schema bootstrap that runs in a store constructor.
        /// </summary>
        public static SqliteConnection OpenConnection(string connectionString)
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = PragmaSql;
            cmd.ExecuteNonQuery();
            return conn;
        }
    }
}
