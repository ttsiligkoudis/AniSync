using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Data.Sqlite;

namespace AnimeList.Services
{
    /// <summary>
    /// SQLite-backed <see cref="IScheduleStore"/>. Shares
    /// <c>anisync.db</c> with the other stores; the
    /// <c>schedule_entries</c> table is created by
    /// <see cref="SqliteConfigStore.EnsureSchema"/> on startup.
    /// </summary>
    public class ScheduleStore : IScheduleStore
    {
        private readonly string _connectionString;

        public ScheduleStore(IConfiguration configuration)
        {
            _connectionString = SqliteConnectionFactory.BuildConnectionString(configuration);
        }

        public async Task UpsertManyAsync(IReadOnlyList<UpcomingEpisode> entries)
        {
            if (entries == null || entries.Count == 0) return;

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // INSERT … ON CONFLICT … DO UPDATE — preserves notified_at so a
            // schedule refresh doesn't re-arm entries the scheduler already
            // dispatched. airing_at can shift (delayed episodes), title /
            // cover may be updated as upstream metadata firms up.
            cmd.CommandText = """
                INSERT INTO schedule_entries
                    (anilist_id, episode, airing_at, title, cover_image, notified_at, refreshed_at)
                VALUES
                    ($anilistId, $episode, $airingAt, $title, $cover, NULL, $now)
                ON CONFLICT(anilist_id, episode) DO UPDATE SET
                    airing_at    = excluded.airing_at,
                    title        = excluded.title,
                    cover_image  = excluded.cover_image,
                    refreshed_at = excluded.refreshed_at;
                """;
            var pAnilistId = cmd.Parameters.Add("$anilistId", SqliteType.Integer);
            var pEpisode = cmd.Parameters.Add("$episode", SqliteType.Integer);
            var pAiringAt = cmd.Parameters.Add("$airingAt", SqliteType.Integer);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pCover = cmd.Parameters.Add("$cover", SqliteType.Text);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            foreach (var e in entries)
            {
                pAnilistId.Value = e.AnilistId;
                pEpisode.Value = e.Episode;
                pAiringAt.Value = e.AiringAt;
                pTitle.Value = e.Title ?? string.Empty;
                pCover.Value = (object)e.CoverImage ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }

        public async Task<List<UpcomingEpisode>> GetPendingAsync()
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Partial index on (airing_at WHERE notified_at IS NULL)
            // backs this — the scheduler only cares about undispatched
            // rows. Order by airing_at so the dispatcher walks them
            // chronologically and the earliest pending fires first.
            cmd.CommandText = """
                SELECT anilist_id, episode, airing_at, title, cover_image
                FROM schedule_entries
                WHERE notified_at IS NULL
                ORDER BY airing_at ASC;
                """;

            var result = new List<UpcomingEpisode>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new UpcomingEpisode(
                    AnilistId: reader.GetInt32(0),
                    Title: reader.GetString(3),
                    Episode: reader.GetInt32(1),
                    AiringAt: reader.GetInt64(2),
                    CoverImage: reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return result;
        }

        public async Task MarkNotifiedAsync(int anilistId, int episode)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE schedule_entries
                SET notified_at = $now
                WHERE anilist_id = $anilistId AND episode = $episode;
                """;
            cmd.Parameters.AddWithValue("$anilistId", anilistId);
            cmd.Parameters.AddWithValue("$episode", episode);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<int> PruneOlderThanAsync(long cutoffUnix)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM schedule_entries WHERE airing_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoffUnix);
            return await cmd.ExecuteNonQueryAsync();
        }
    }
}
