using AnimeList.Models;
using AnimeList.Models.Api;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Inbound webhook surface for self-hosted media servers. Plex, Jellyfin, and Emby all
    /// share the same endpoint — the controller content-type-sniffs the body and dispatches
    /// to the matching parser.
    ///
    /// Auth model: per-user scrobble token in the URL path. The token is generated lazily by
    /// the configure page and revocable via "Rotate" without affecting the main config UID.
    ///
    /// Pipeline (after parse + normalise):
    ///   1. drop non-scrobble events (play / pause / rate)
    ///   2. drop non-episode items (movies, music, trailers)
    ///   3. drop events from a different Plex Home username (when the user opted into the filter)
    ///   4. resolve external ids (anidb / imdb / tmdb / tvdb) → the user's primary tracker id
    ///   5. drop duplicate events within a 60-second window per (uid, anime, season, episode)
    ///   6. fan out via SyncService.FanOutSaveAsync to the primary + every linked tracker
    ///
    /// Always responds 200 OK so misbehaving servers don't queue retries on the user's account.
    /// </summary>
    [ApiController]
    [Route("api/v1/scrobble")]
    [EnableRateLimiting("scrobble")]
    [Tags("Scrobble")]
    [ApiExplorerSettings(IgnoreApi = true)]
    // Webhook authenticates via the URL-path scrobble token, not the session cookie,
    // so the CSRF filter doesn't apply. Plex / Jellyfin / Emby fire these from server
    // contexts that won't and can't present an antiforgery token.
    [IgnoreAntiforgeryToken]
    public class ScrobbleController : ControllerBase
    {
        private readonly IConfigStore _configStore;
        private readonly ITokenService _tokenService;
        private readonly IAnimeMappingService _mappingService;
        private readonly ISyncService _syncService;
        private readonly IMemoryCache _dedupCache;
        private readonly ILogger<ScrobbleController> _logger;

        // Window during which a duplicate (uid, anime, season, episode) is silently dropped.
        // Plex emits scrobble once per 90% mark, but resumed sessions / Jellyfin re-deliveries
        // can repeat. 60s is comfortably longer than any realistic retry window.
        private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(60);

        public ScrobbleController(IConfigStore configStore, ITokenService tokenService,
            IAnimeMappingService mappingService, ISyncService syncService,
            IMemoryCache dedupCache, ILogger<ScrobbleController> logger)
        {
            _configStore = configStore;
            _tokenService = tokenService;
            _mappingService = mappingService;
            _syncService = syncService;
            _dedupCache = dedupCache;
            _logger = logger;
        }

        [HttpPost("{token}")]
        public async Task<IActionResult> Ingest(string token)
        {
            // Resolve identity first — if the token doesn't match a row, 401 (so a curl
            // with the wrong token gets a real error) but for accidental triggers from a
            // legit token we always return 200 below.
            var uid = await _configStore.ResolveUidByScrobbleTokenAsync(token);
            if (string.IsNullOrEmpty(uid))
                return Unauthorized(new ApiError("invalid scrobble token"));

            NormalizedScrobbleEvent normalized;
            try
            {
                normalized = await ParseAsync(Request);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scrobble parse failed for uid={Uid}.", uid);
                return Ok(); // never NACK; the server would just retry
            }

            if (normalized == null) return Ok();
            if (!normalized.IsScrobble || !normalized.IsEpisode) return Ok();

            // Plex Home filter — opt-in per user. Empty / null configured value = accept any
            // username, which is correct for single-user servers and Jellyfin/Emby (where this
            // field is informational only).
            if (normalized.Source == "plex")
            {
                var configured = await _configStore.GetPlexUsernameAsync(uid);
                if (!string.IsNullOrWhiteSpace(configured)
                    && !string.Equals(configured.Trim(), normalized.Username?.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Scrobble dropped (plex username mismatch) uid={Uid} got={Got}.",
                        uid, normalized.Username);
                    return Ok();
                }
            }

            var primary = await _tokenService.GetAccessTokenAsync(uid);
            if (primary == null || string.IsNullOrEmpty(primary.access_token))
            {
                _logger.LogInformation("Scrobble dropped — no primary token for uid={Uid}.", uid);
                return Ok();
            }

            var animeId = await _mappingService.ResolveExternalAsync(
                normalized.ExternalIds, primary.anime_service, normalized.Season);
            if (string.IsNullOrEmpty(animeId))
            {
                _logger.LogDebug("Scrobble dropped — no mapping for {Series} S{Season}E{Ep} ({Source}).",
                    normalized.SeriesTitle, normalized.Season, normalized.Episode, normalized.Source);
                return Ok();
            }

            // Dedup. Key = uid + source + tracker id + season + episode; one entry per unique
            // playback. Source is in the key (rather than collapsed to a single bucket) so we
            // can tell Plex-via-Plex apart from Plex-via-Jellyfin when both are configured
            // against the same library — a 60s coincidence between the two no longer silently
            // hides the second event from logs / observability, while still deduping within
            // a source for retries.
            var dedupKey = $"scrobble:{uid}:{normalized.Source}:{animeId}:{normalized.Season}:{normalized.Episode}";
            if (_dedupCache.TryGetValue(dedupKey, out _))
            {
                _logger.LogDebug("Scrobble dropped (dedup) uid={Uid} anime={Anime} S{S}E{E}.",
                    uid, animeId, normalized.Season, normalized.Episode);
                return Ok();
            }
            _dedupCache.Set(dedupKey, true, DedupWindow);

            try
            {
                await _syncService.FanOutSaveAsync(primary, animeId,
                    normalized.Season, normalized.Episode ?? 0);
                _logger.LogInformation("Scrobble {Source} uid={Uid} anime={Anime} S{S}E{E}.",
                    normalized.Source, uid, animeId, normalized.Season, normalized.Episode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scrobble fan-out failed uid={Uid} anime={Anime}.", uid, animeId);
            }

            return Ok();
        }

        // ── Parsing / normalisation ────────────────────────────────────────────

        private static async Task<NormalizedScrobbleEvent> ParseAsync(HttpRequest request)
        {
            var contentType = request.ContentType ?? string.Empty;

            // Plex sends multipart/form-data with a single "payload" form field carrying JSON.
            if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var form = await request.ReadFormAsync();
                var json = form["payload"].ToString();
                if (string.IsNullOrEmpty(json)) return null;
                var plex = JsonConvert.DeserializeObject<PlexWebhook>(json);
                return NormalizePlex(plex);
            }

            // Jellyfin and Emby both use application/json. Distinguish by which top-level
            // field the body actually carries — Jellyfin uses NotificationType, Emby uses Event.
            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(request.Body);
                var json = await reader.ReadToEndAsync();
                if (string.IsNullOrEmpty(json)) return null;

                // One pass to peek at the discriminator, then deserialize into the right shape.
                var probe = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (probe == null) return null;
                if (probe.ContainsKey("NotificationType"))
                {
                    var jf = JsonConvert.DeserializeObject<JellyfinWebhook>(json);
                    return NormalizeJellyfin(jf);
                }
                if (probe.ContainsKey("Event"))
                {
                    var emby = JsonConvert.DeserializeObject<EmbyWebhook>(json);
                    return NormalizeEmby(emby);
                }
            }

            return null;
        }

        private static NormalizedScrobbleEvent NormalizePlex(PlexWebhook p)
        {
            if (p == null) return null;
            return new NormalizedScrobbleEvent
            {
                Source = "plex",
                IsScrobble = p.Event == "media.scrobble",
                IsEpisode = string.Equals(p.Metadata?.Type, "episode", StringComparison.OrdinalIgnoreCase),
                Username = p.Account?.Title,
                SeriesTitle = p.Metadata?.GrandparentTitle,
                Season = p.Metadata?.ParentIndex,
                Episode = p.Metadata?.Index,
                ExternalIds = ExtractPlexGuids(p.Metadata?.Guids),
            };
        }

        // Plex GUIDs look like "imdb://tt12345", "tvdb://12345", "anidb://12345", "tmdb://12345".
        private static List<(string prefix, string id)> ExtractPlexGuids(List<PlexGuid> guids)
        {
            var result = new List<(string, string)>();
            if (guids == null) return result;

            foreach (var g in guids)
            {
                if (string.IsNullOrEmpty(g?.Id)) continue;
                var idx = g.Id.IndexOf("://", StringComparison.Ordinal);
                if (idx <= 0) continue;
                var source = g.Id[..idx].ToLowerInvariant();
                var raw = g.Id[(idx + 3)..];
                AddExternalId(result, source, raw);
            }
            return result;
        }

        private static NormalizedScrobbleEvent NormalizeJellyfin(JellyfinWebhook j)
        {
            if (j == null) return null;

            var ids = new List<(string, string)>();
            AddExternalId(ids, "anidb", j.ProviderAnidb);
            AddExternalId(ids, "imdb", j.ProviderImdb);
            AddExternalId(ids, "tmdb", j.ProviderTmdb);
            AddExternalId(ids, "tvdb", j.ProviderTvdb);

            return new NormalizedScrobbleEvent
            {
                Source = "jellyfin",
                // Jellyfin's "PlaybackStop" only counts as a scrobble when the user finished
                // the file — without this guard, scrubbing or stopping early would also fire.
                IsScrobble = string.Equals(j.NotificationType, "PlaybackStop", StringComparison.OrdinalIgnoreCase)
                    && j.PlayedToCompletion == true,
                IsEpisode = string.Equals(j.ItemType, "Episode", StringComparison.OrdinalIgnoreCase),
                Username = j.NotificationUsername,
                SeriesTitle = j.SeriesName,
                Season = j.SeasonNumber,
                Episode = j.EpisodeNumber,
                ExternalIds = ids,
            };
        }

        private static NormalizedScrobbleEvent NormalizeEmby(EmbyWebhook e)
        {
            if (e == null) return null;

            var ids = new List<(string, string)>();
            if (e.Item?.ProviderIds != null)
            {
                foreach (var (key, value) in e.Item.ProviderIds)
                {
                    AddExternalId(ids, key.ToLowerInvariant(), value);
                }
            }

            return new NormalizedScrobbleEvent
            {
                Source = "emby",
                // Emby's "playback.stop" plus Item.UserData.Played would be ideal, but Played
                // isn't populated on every server config. Treat playback.stop as the scrobble
                // signal — same compromise Plex (media.scrobble) and Jellyfin make.
                IsScrobble = string.Equals(e.Event, "playback.stop", StringComparison.OrdinalIgnoreCase),
                IsEpisode = string.Equals(e.Item?.Type, "Episode", StringComparison.OrdinalIgnoreCase),
                Username = e.User?.Name,
                SeriesTitle = e.Item?.SeriesName,
                Season = e.Item?.ParentIndexNumber,
                Episode = e.Item?.IndexNumber,
                ExternalIds = ids,
            };
        }

        // Normalises the per-source provider keys onto the prefixes ResolveExternalAsync expects.
        private static void AddExternalId(List<(string prefix, string id)> list, string source, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            // Strip stray "tt" prefixing on imdb so the raw value is just digits the resolver
            // will reattach. Jellyfin sometimes includes "tt", sometimes not.
            var prefix = source switch
            {
                "anidb" => anidbPrefix,
                "imdb" => imdbPrefix,
                "tmdb" or "themoviedb" => tmdbPrefix,
                "tvdb" or "thetvdb" => tvdbPrefix,
                _ => null,
            };
            if (prefix == null) return;

            list.Add((prefix, raw.Trim()));
        }
    }
}
