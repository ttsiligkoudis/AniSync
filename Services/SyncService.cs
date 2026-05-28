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
        private readonly ITokenService _tokenService;
        private readonly IUserListCache _listCache;
        private readonly ILogger<SyncService> _logger;

        public SyncService(IConfigStore configStore, IAnilistService anilistService,
            IKitsuService kitsuService, IMalService malService, ITokenService tokenService,
            IUserListCache listCache, ILogger<SyncService> logger)
        {
            _configStore = configStore;
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _tokenService = tokenService;
            _listCache = listCache;
            _logger = logger;
        }

        public async Task SaveProgressAndFanOutAsync(TokenData primary, string animeId, int? season, int episode)
        {
            switch (primary.anime_service)
            {
                case AnimeService.Anilist:
                    await _anilistService.SaveAnimeEntryAsync(primary, animeId, season, episode);
                    break;
                case AnimeService.MyAnimeList:
                    await _malService.SaveAnimeEntryAsync(primary, animeId, season, episode);
                    break;
                default:
                    await _kitsuService.SaveAnimeEntryAsync(primary, animeId, season, episode);
                    break;
            }
            // Scrobble auto-track: the episode IS the progress (every webhook fires at the
            // end of an episode), so it lines up with FanOutSaveAsync's `progress` slot. The
            // parameter naming below would otherwise mislead the next reader into "fixing"
            // this to a separate variable and breaking the scrobble path.
            await FanOutSaveAsync(primary, animeId, season, progress: episode);
        }

        public async Task<FanOutSaveResult> FanOutSaveAsync(TokenData primary, string animeId, int? season, int progress,
            string status = null, double? score = null, string notes = null, int? rewatchCount = null,
            DateTime? startedAt = null, DateTime? finishedAt = null)
        {
            if (primary == null || primary.anonymousUser) return FanOutSaveResult.Empty;
            var uid = await _configStore.UpsertAsync(primary);
            var linkedTokens = await GetActiveLinkedTokensAsync(uid);
            if (linkedTokens.Count == 0) return FanOutSaveResult.Empty;

            // Normalise the source-side ListType once so each per-target call doesn't have
            // to re-parse it, and normalise the score to 0-10 since AniList primaries can
            // hand us 0-100.
            var sourceListType = ParseStatusToListType(status, primary.anime_service);
            var normalisedScore = NormaliseScoreToTen(score, primary.anime_service);

            var tasks = linkedTokens.Select(linked =>
                SaveToProviderAsync(uid, linked, sourceListType, animeId, season, progress,
                    normalisedScore, notes, rewatchCount, startedAt, finishedAt));

            var outcomes = await Task.WhenAll(tasks);

            var result = new FanOutSaveResult();
            for (int i = 0; i < linkedTokens.Count; i++)
            {
                if (outcomes[i]) result.Succeeded.Add(linkedTokens[i].Service);
                else result.Failed.Add(linkedTokens[i].Service);
            }
            return result;
        }

        public async Task FanOutDeleteAsync(TokenData primary, string animeId, int? season)
        {
            if (primary == null || primary.anonymousUser) return;
            var uid = await _configStore.UpsertAsync(primary);
            var linkedTokens = await GetActiveLinkedTokensAsync(uid);
            if (linkedTokens.Count == 0) return;

            var tasks = linkedTokens.Select(linked => DeleteFromProviderAsync(uid, linked, animeId, season));
            await Task.WhenAll(tasks);
        }

        public async Task<List<AnimeEntry>> GetPrimaryEntriesAsync(TokenData primary)
        {
            if (primary == null || primary.anonymousUser) return [];
            return primary.anime_service switch
            {
                AnimeService.Anilist => await _anilistService.GetUserListEntriesAsync(primary),
                AnimeService.Kitsu => await _kitsuService.GetUserListEntriesAsync(primary),
                AnimeService.MyAnimeList => await _malService.GetUserListEntriesAsync(primary),
                _ => [],
            };
        }

        private async Task<List<LinkedToken>> GetActiveLinkedTokensAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return [];
            var linked = await _configStore.GetLinkedTokensAsync(uid);
            var active = new List<LinkedToken>();

            foreach (var l in linked)
            {
                // Skip already-broken links so the refresh below doesn't re-loop on each save.
                if (l.NeedsReauth || l.TokenData == null) continue;

                // Lazy refresh on the boundary: linked tokens never go through the primary's
                // GetAccessTokenAsync path, so we have to handle expiry ourselves. If refresh
                // fails — token revoked, password rotated on Kitsu, etc. — flip the
                // NeedsReauth flag so the UI can prompt the user to re-link and the next
                // fan-out skips this provider cleanly.
                if (IsTokenExpired(l.TokenData.expiration_date))
                {
                    var refreshed = await TryRefreshAsync(l.TokenData);
                    if (refreshed == null || string.IsNullOrEmpty(refreshed.access_token))
                    {
                        l.NeedsReauth = true;
                        await _configStore.SetLinkedTokenAsync(uid, l);
                        continue;
                    }
                    l.TokenData = refreshed;
                    await _configStore.SetLinkedTokenAsync(uid, l);
                }

                active.Add(l);
            }

            return active;
        }

        private async Task TryRefreshAsync(string uid, LinkedToken target)
        {
            // Trigger a forced re-issue (Kitsu re-auths via stored creds; AniList/MAL hit
            // the OAuth refresh endpoint). Used when a save fails 401 even though the
            // expiration date said the token was still good — server-side revocation.
            var refreshed = await TryRefreshAsync(target.TokenData);
            if (refreshed != null && !string.IsNullOrEmpty(refreshed.access_token))
            {
                target.TokenData = refreshed;
                await _configStore.SetLinkedTokenAsync(uid, target);
            }
            else
            {
                target.NeedsReauth = true;
                await _configStore.SetLinkedTokenAsync(uid, target);
            }
        }

        private async Task<TokenData> TryRefreshAsync(TokenData token)
        {
            try { return await _tokenService.RefreshLinkedTokenAsync(token); }
            catch { return null; }
        }

        private async Task<bool> SaveToProviderAsync(string uid, LinkedToken target, ListType? sourceListType,
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
                // Drop any cached list state we have for this linked tracker so a
                // dashboard render driven by that account (multi-link union, or the
                // user logging in as the linked identity) reflects the fan-out
                // write. Scrobble-only paths route through here, so this is the
                // sole invalidation point that covers webhook writes.
                _listCache.Invalidate(target.TokenData);
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                // Server-side revocation — the token's date said valid but the API rejected
                // it. Try one immediate refresh; if that also fails, flag NeedsReauth so the
                // UI surfaces it and future fan-outs skip the link.
                _logger.LogWarning(ex, "Sync {Service} save 401 — flagging for re-auth.", target.Service);
                await TryRefreshAsync(uid, target);
                return false;
            }
            catch (Exception ex)
            {
                // Best-effort sync. Log so a deploy log diagnoses why a particular target's
                // save fell over (mapping gap, transient 5xx, etc.) without failing the
                // primary save the user is actually waiting on.
                _logger.LogError(ex, "Sync {Service} save failed.", target.Service);
                return false;
            }
        }

        private async Task DeleteFromProviderAsync(string uid, LinkedToken target, string animeId, int? season)
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
                _listCache.Invalidate(target.TokenData);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Sync {Service} delete 401 — flagging for re-auth.", target.Service);
                await TryRefreshAsync(uid, target);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync {Service} delete failed.", target.Service);
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
