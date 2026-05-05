using AnimeList.Models;
using AnimeList.Services.Interfaces;
using System.Collections.Concurrent;

namespace AnimeList.Services
{
    public class SyncJobService : ISyncJobService
    {
        // Per-(uid) job status. Static so it survives the Scoped service lifetime — every
        // SyncStatus poll instantiates a fresh SyncJobService, but the dictionary is shared.
        private static readonly ConcurrentDictionary<string, SyncJobStatus> _jobs = new();

        // Cap the per-job concurrency so one user's full sync doesn't trip Kitsu's rate
        // limit (and so a 1000-entry library doesn't fire 3000 simultaneous Save calls
        // against the wider linked-provider set).
        private const int FanOutConcurrency = 4;

        private readonly IServiceScopeFactory _scopeFactory;

        public SyncJobService(IServiceScopeFactory scopeFactory)
        {
            // We have to capture the scope factory rather than the per-request services
            // because the background Task outlives the request that started it.
            _scopeFactory = scopeFactory;
        }

        public bool TryStart(string uid, TokenData primary)
        {
            if (string.IsNullOrEmpty(uid) || primary == null) return false;

            var status = new SyncJobStatus
            {
                Running = true,
                StartedAt = DateTime.UtcNow,
                Message = "Starting…",
            };

            // CompareExchange semantics via TryAdd-then-replace: only start if no running
            // job exists for this uid. If a previous run finished, replace its status.
            if (_jobs.TryGetValue(uid, out var existing) && existing.Running)
                return false;

            _jobs[uid] = status;

            // Fire-and-forget. Each iteration creates its own DI scope so the per-request
            // ITokenService/SyncService etc. work — Task.Run runs outside the original
            // request scope, so we can't reuse the captured services directly.
            _ = Task.Run(() => RunAsync(uid, primary, status));
            return true;
        }

        public SyncJobStatus GetStatus(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            return _jobs.TryGetValue(uid, out var s) ? s : null;
        }

        private async Task RunAsync(string uid, TokenData primary, SyncJobStatus status)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var anilist = scope.ServiceProvider.GetRequiredService<IAnilistService>();
                var kitsu = scope.ServiceProvider.GetRequiredService<IKitsuService>();
                var mal = scope.ServiceProvider.GetRequiredService<IMalService>();
                var sync = scope.ServiceProvider.GetRequiredService<ISyncService>();

                status.Message = "Fetching primary library…";
                var primaryEntries = primary.anime_service switch
                {
                    AnimeService.Anilist => await anilist.GetUserListEntriesAsync(primary),
                    AnimeService.Kitsu => await kitsu.GetUserListEntriesAsync(primary),
                    AnimeService.MyAnimeList => await mal.GetUserListEntriesAsync(primary),
                    _ => new List<AnimeEntry>(),
                };

                status.Total = primaryEntries.Count;
                status.Message = primaryEntries.Count == 0
                    ? "Primary library is empty."
                    : $"Syncing 0 / {primaryEntries.Count}";

                if (primaryEntries.Count == 0)
                {
                    status.Running = false;
                    status.FinishedAt = DateTime.UtcNow;
                    return;
                }

                // SemaphoreSlim caps in-flight fan-outs so the cumulative request rate stays
                // polite across all linked providers. Inside FanOutSaveAsync each linked
                // target is itself written sequentially, so total in-flight requests is
                // FanOutConcurrency × <linked count>.
                using var sem = new SemaphoreSlim(FanOutConcurrency);
                var tasks = primaryEntries.Select(entry => RunOneAsync(sync, sem, status, primary, entry, primaryEntries.Count)).ToList();
                await Task.WhenAll(tasks);

                status.Message = status.Failed == 0
                    ? $"Done — {status.Completed} synced."
                    : $"Done — {status.Completed} synced, {status.Failed} failed (see logs).";
            }
            catch (Exception ex)
            {
                status.Message = $"Failed: {ex.Message}";
                Console.Error.WriteLine($"[SyncJob] {uid} failed: {ex}");
            }
            finally
            {
                status.Running = false;
                status.FinishedAt = DateTime.UtcNow;
            }
        }

        private static async Task RunOneAsync(ISyncService sync, SemaphoreSlim sem, SyncJobStatus status,
            TokenData primary, AnimeEntry entry, int total)
        {
            await sem.WaitAsync();
            try
            {
                await sync.FanOutSaveAsync(primary, entry.MediaId, season: null, entry.Progress,
                    entry.Status, entry.Score, entry.Notes, entry.RewatchCount,
                    entry.StartedAt, entry.FinishedAt);
                lock (status) status.Completed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SyncJob] entry {entry.MediaId} failed: {ex.Message}");
                lock (status) status.Failed++;
            }
            finally
            {
                sem.Release();
            }

            // The progress message lags by exactly one increment (we update after the field
            // bumps), which is fine — the UI poll cadence is much coarser than per-item work.
            status.Message = $"Syncing {status.Completed + status.Failed} / {total}";
        }
    }
}
