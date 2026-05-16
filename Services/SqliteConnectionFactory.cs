using Microsoft.Data.Sqlite;

namespace AnimeList.Services
{
    /// <summary>
    /// Shared connection-string builder for the SQLite stores that all sit
    /// on the same <c>anisync.db</c> file (<see cref="SqliteConfigStore"/>,
    /// <see cref="NotificationStore"/>, <see cref="WatchingCacheStore"/>).
    /// Centralises the <c>ANISYNC_DATA_DIR</c> resolution so a new store
    /// doesn't have to replicate the fallback + pooling + shared-cache
    /// settings.
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
                Cache = SqliteCacheMode.Shared,
            }.ToString();
        }
    }
}
