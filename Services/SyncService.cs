using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    public class SyncService : ISyncService
    {
        private readonly IConfigStore _configStore;
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;

        public SyncService(IConfigStore configStore, IAnilistService anilistService,
            IKitsuService kitsuService, IMalService malService)
        {
            _configStore = configStore;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
        }

        public async Task FanOutSaveAsync(TokenData primary, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null)
        {
            var linkedTokens = await GetActiveLinkedTokensAsync(primary);
            if (linkedTokens.Count == 0) return;

            // Normalise the source-side ListType once so each per-target call doesn't have
            // to re-parse it, and normalise the score to 0-10 since AniList primaries can
            // hand us 0-100.
            var sourceListType = ParseStatusToListType(status, primary.anime_service);
            var normalisedScore = NormaliseScoreToTen(score, primary.anime_service);

            var tasks = linkedTokens.Select(linked =>
                SaveToProviderAsync(linked, sourceListType, animeId, season, progress,
                    normalisedScore, notes, rewatchCount, startedAt, finishedAt));

            await Task.WhenAll(tasks);
        }

        public async Task FanOutDeleteAsync(TokenData primary, string animeId, int? season)
        {
            var linkedTokens = await GetActiveLinkedTokensAsync(primary);
            if (linkedTokens.Count == 0) return;

            var tasks = linkedTokens.Select(linked => DeleteFromProviderAsync(linked, animeId, season));
            await Task.WhenAll(tasks);
        }

        private async Task<List<LinkedToken>> GetActiveLinkedTokensAsync(TokenData primary)
        {
            if (primary == null || primary.anonymousUser) return [];
            // UpsertAsync is idempotent — same UID for re-logins of the same user — so we
            // use it instead of a dedicated "find UID" lookup.
            var uid = await _configStore.UpsertAsync(primary);
            var linked = await _configStore.GetLinkedTokensAsync(uid);
            // Skip tokens flagged as needing re-auth so a stale token doesn't 401-loop on
            // every save. The user re-links via the configure page and writes resume.
            return linked.Where(l => !l.NeedsReauth).ToList();
        }

        private async Task SaveToProviderAsync(LinkedToken target, ListType? sourceListType,
            string animeId, int? season, int progress, double? score, string notes,
            int? rewatchCount, DateTime? startedAt, DateTime? finishedAt)
        {
            try
            {
                var targetStatus = TranslateStatus(sourceListType, target.Service, target.TokenData);

                switch (target.Service)
                {
                    case AnimeService.Anilist:
                        await _anilistService.SaveAnimeEntryAsync(target.TokenData, animeId, season, progress,
                            targetStatus, score, notes, rewatchCount, startedAt, finishedAt);
                        break;
                    case AnimeService.Kitsu:
                        await _kitsuService.SaveAnimeEntryAsync(target.TokenData, animeId, season, progress,
                            targetStatus, score, notes, rewatchCount, startedAt, finishedAt);
                        break;
                    case AnimeService.MyAnimeList:
                        await _malService.SaveAnimeEntryAsync(target.TokenData, animeId, season, progress,
                            targetStatus, score, notes, rewatchCount, startedAt, finishedAt);
                        break;
                }
            }
            catch
            {
                // Best-effort sync. A single linked provider failing — token expired,
                // mapping gap, transient API error — must not fail the primary save.
                // Tier 3 hooks NeedsReauth handling for the 401 case.
            }
        }

        private async Task DeleteFromProviderAsync(LinkedToken target, string animeId, int? season)
        {
            try
            {
                switch (target.Service)
                {
                    case AnimeService.Anilist:
                        await _anilistService.DeleteAnimeEntryAsync(target.TokenData, animeId, season);
                        break;
                    case AnimeService.Kitsu:
                        await _kitsuService.DeleteAnimeEntryAsync(target.TokenData, animeId, season);
                        break;
                    case AnimeService.MyAnimeList:
                        await _malService.DeleteAnimeEntryAsync(target.TokenData, animeId, season);
                        break;
                }
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>
        /// Resolves the target service's status string from a normalised ListType. Returns
        /// null when the source had no status (e.g. auto-track), so each target service
        /// preserves its existing entry status. MAL's REPEATING degrades through the
        /// synthetic "rewatching" sentinel that <see cref="MalService"/> turns into the
        /// (status=watching, is_rewatching=true) pair.
        /// </summary>
        private static string TranslateStatus(ListType? sourceListType, AnimeService targetService, TokenData targetTokenData)
        {
            if (!sourceListType.HasValue) return null;

            // Kitsu has no rewatching concept; degrade REPEATING to "current" so the entry
            // ends up where the user expects rather than failing silently.
            if (sourceListType.Value == ListType.Repeating && targetService == AnimeService.Kitsu)
                return GetListTypeString(ListType.Current, targetTokenData);

            // For MAL, the synthetic "rewatching" string is what MalService.SaveAnimeEntryAsync
            // recognises and translates into status=watching + is_rewatching=true on the wire.
            if (sourceListType.Value == ListType.Repeating && targetService == AnimeService.MyAnimeList)
                return "rewatching";

            return GetListTypeString(sourceListType.Value, targetTokenData);
        }

        /// <summary>
        /// Reverse-maps a service-native status string back to the shared ListType enum.
        /// MAL's "rewatching" sentinel (sent by the UI for the synthetic Repeating state)
        /// is mapped to ListType.Repeating so the cross-service translation lands correctly.
        /// </summary>
        private static ListType? ParseStatusToListType(string status, AnimeService source)
        {
            if (string.IsNullOrEmpty(status)) return null;

            return source switch
            {
                AnimeService.Anilist => status.ToUpperInvariant() switch
                {
                    "CURRENT" => ListType.Current,
                    "COMPLETED" => ListType.Completed,
                    "PLANNING" => ListType.Planning,
                    "PAUSED" => ListType.Paused,
                    "DROPPED" => ListType.Dropped,
                    "REPEATING" => ListType.Repeating,
                    _ => null,
                },
                AnimeService.Kitsu => status.ToLowerInvariant() switch
                {
                    "current" => ListType.Current,
                    "completed" => ListType.Completed,
                    "planned" => ListType.Planning,
                    "on_hold" => ListType.Paused,
                    "dropped" => ListType.Dropped,
                    _ => null,
                },
                AnimeService.MyAnimeList => status.ToLowerInvariant() switch
                {
                    "watching" => ListType.Current,
                    "completed" => ListType.Completed,
                    "plan_to_watch" => ListType.Planning,
                    "on_hold" => ListType.Paused,
                    "dropped" => ListType.Dropped,
                    "rewatching" => ListType.Repeating,
                    _ => null,
                },
                _ => null,
            };
        }

        /// <summary>
        /// Normalises the user-entered score to a 0-10 scale before fan-out. AniList users
        /// can be on 0-100 (~half of accounts default to it); other services are 0-10. The
        /// rare 0-5 AniList configuration is not detectable without an extra API call and
        /// degrades to a half-value here — documented limitation.
        /// </summary>
        private static double? NormaliseScoreToTen(double? score, AnimeService source)
        {
            if (!score.HasValue) return null;
            if (source == AnimeService.Anilist && score.Value > 10) return score.Value / 10.0;
            return score.Value;
        }
    }
}
