using AnimeList.Models;
using AnimeList.Services.Interfaces;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net.Http.Headers;

namespace AnimeList.Services
{
    /// <summary>
    /// Resolves anime IDs across services (AniList, Kitsu, MAL, IMDb, TMDB, TVDB, AniDB) using
    /// community mapping data. Combines Fribb/anime-lists with manami-project/anime-offline-database
    /// for broader coverage, and enriches entries on demand via the TMDB external_ids API.
    /// Registered as a singleton; caches the full mapping in memory for 24 hours.
    ///
    /// TVDB and AniDB are populated from Fribb's full mapping file (the mini variant strips them);
    /// the Plex/Jellyfin webhook ingestion path needs them because Plex's default agent and HAMA
    /// emit those IDs respectively.
    /// </summary>
    public class AnimeMappingService : IAnimeMappingService
    {
        private const string FribbMappingUrl = "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-full.json";
        private const string OfflineDbUrl = "https://raw.githubusercontent.com/manami-project/anime-offline-database/master/anime-offline-database-minified.json";
        private const string TmdbApiBase = "https://api.themoviedb.org/3";

        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

        private readonly IHttpClientFactory _clientFactory;
        private readonly IConfiguration _configuration;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly ConcurrentDictionary<string, string> _tmdbToImdbCache = new();
        private ConcurrentDictionary<string, List<AnimeIdMapping>> _enrichedImdbIndex = new();
        private FrozenDictionary<int, AnimeIdMapping> _anilistMapping;
        private FrozenDictionary<int, AnimeIdMapping> _kitsuMapping;
        private FrozenDictionary<int, AnimeIdMapping> _malMapping;
        private ConcurrentDictionary<string, List<AnimeIdMapping>> _imdbMapping = new();
        private FrozenDictionary<string, List<AnimeIdMapping>> _tmdbMapping;
        private FrozenDictionary<int, List<AnimeIdMapping>> _tvdbMapping;
        private FrozenDictionary<int, AnimeIdMapping> _anidbMapping;
        private DateTime _lastLoaded = DateTime.MinValue;
        private readonly string _diskCachePath;

        // Network timeout for the mapping-source fetches. HttpClient defaults to
        // 100 s, which makes "raw.githubusercontent.com is misbehaving today"
        // look like a hang to every caller. 15 s is enough for a healthy ~10 MB
        // download on a slow link and short enough that a first user request
        // after a Fly cold start with bad upstream connectivity errors out fast
        // and falls back to whatever's in the on-disk snapshot.
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

        public AnimeMappingService(IHttpClientFactory clientFactory, IConfiguration configuration)
        {
            _clientFactory = clientFactory;
            _configuration = configuration;

            // Persistent snapshot of the merged mapping list. Written after every
            // successful network load, read as a fallback when the network load
            // fails on a cold start. Lives next to anisync.db on the Fly volume.
            var dataDir = configuration["ANISYNC_DATA_DIR"]
                ?? Environment.GetEnvironmentVariable("ANISYNC_DATA_DIR")
                ?? ".";
            _diskCachePath = Path.Combine(dataDir, "anime-mappings.json");
        }

        public async Task<AnimeIdMapping> GetAnilistMapping(string anilistId)
        {
            await EnsureMappingsLoadedAsync();
            if (!_anilistMapping.TryGetValue(int.Parse(anilistId.Replace(anilistPrefix, "")), out var mapping))
                return null;

            await TryEnrichImdbAsync(mapping);
            return mapping;
        }

        public async Task<AnimeIdMapping> GetKitsuMapping(string kitsuId)
        {
            await EnsureMappingsLoadedAsync();
            if (!_kitsuMapping.TryGetValue(int.Parse(kitsuId.Replace(kitsuPrefix, "")), out var mapping))
                return null;

            await TryEnrichImdbAsync(mapping);
            return mapping;
        }

        public async Task<AnimeIdMapping> GetMalMapping(string malId)
        {
            await EnsureMappingsLoadedAsync();
            if (!int.TryParse(malId.Replace(malPrefix, ""), out var parsed)) return null;
            if (!_malMapping.TryGetValue(parsed, out var mapping)) return null;

            await TryEnrichImdbAsync(mapping);
            return mapping;
        }

        public async Task<List<AnimeIdMapping>> GetImdbMapping(string imdb, int? season = null)
        {
            await EnsureMappingsLoadedAsync();
            var entries = new List<AnimeIdMapping>();

            if (_imdbMapping.TryGetValue(imdb, out var mappingEntries))
                entries.AddRange(mappingEntries);

            return entries
                .DistinctBy(e => (e.AnilistId, e.KitsuId))
                .Where(m => !season.HasValue || m.Season == season)
                .ToList();
        }

        public async Task<List<AnimeIdMapping>> GetTmdbMapping(string tmdbId, int? season = null)
        {
            await EnsureMappingsLoadedAsync();
            var entries = (_tmdbMapping.TryGetValue(tmdbId.Replace(tmdbPrefix, ""), out var mappings) ? mappings : []) ?? [];
            return entries.Where(m => !season.HasValue || m.Season == season).ToList();
        }

        public async Task EnsureLoadedAsync()
        {
            await EnsureMappingsLoadedAsync();
        }

        public async Task EnrichImdbMappings(List<AnimeIdMapping> mappings)
        {
            if (mappings?.Any() != true) return;

            // The mapping objects are shared references between the input list and
            // the cached `_imdbMapping[key]` list, so mutations on `mapping.Name` /
            // `mapping.Episodes` (the only thing callers actually enrich) already
            // propagate to the cache without us touching the dictionary. The job
            // left for this method is to *add* any input mappings that aren't yet
            // in the cached bucket — never to overwrite the bucket with the input
            // subset.
            //
            // The earlier implementation called
            // `_imdbMapping.AddOrUpdate(key, _, (_, _) => group.ToList())`, which
            // REPLACED the whole bucket with whatever was passed in. When
            // MetaController.BuildEntrySeasonAsync invokes us with a single
            // per-cour mapping (after fetching its summary), that wiped every
            // sibling cour from the bucket. The next `GetImdbMapping(imdb)`
            // call then returned just one mapping, BuildSeasonsAsync took the
            // single-mapping branch, and the manage-entry modal collapsed a
            // multi-season franchise to a single-cour picker (visible after
            // the first close + reopen).
            foreach (var group in mappings.GroupBy(g => g.ImdbId))
            {
                if (string.IsNullOrEmpty(group.Key)) continue;
                var input = group.ToList();
                _imdbMapping.AddOrUpdate(group.Key,
                    _ => input,
                    (_, existing) =>
                    {
                        var merged = new List<AnimeIdMapping>(existing);
                        foreach (var m in input)
                        {
                            if (!merged.Any(e => e.AnilistId == m.AnilistId && e.KitsuId == m.KitsuId))
                            {
                                merged.Add(m);
                            }
                        }
                        return merged;
                    });
            }
        }

        public async Task<AnimeSourceLinks> BuildSourceLinksAsync(string animeId)
        {
            var links = new AnimeSourceLinks();
            if (string.IsNullOrEmpty(animeId)) return links;

            try
            {
                // 1. Seed from the self id when it's in a service-native id
                //    space. Detail page handoffs always carry a service-native
                //    id at this point; the IMDb / TMDB branches stay for
                //    direct deep-links (e.g. when Stremio's grouped-cour card
                //    hands the user to /anime/tt5311514).
                AnimeIdMapping mapping = null;
                if (animeId.StartsWith(anilistPrefix)
                    && int.TryParse(animeId[anilistPrefix.Length..], out var aId))
                {
                    links.AnilistId = aId;
                    mapping = await GetAnilistMapping(animeId);
                }
                else if (animeId.StartsWith(malPrefix)
                    && int.TryParse(animeId[malPrefix.Length..], out var mId))
                {
                    links.MalId = mId;
                    mapping = await GetMalMapping(animeId);
                }
                else if (animeId.StartsWith(kitsuPrefix)
                    && int.TryParse(animeId[kitsuPrefix.Length..], out var kId))
                {
                    links.KitsuId = kId;
                    mapping = await GetKitsuMapping(animeId);
                }
                else if (animeId.StartsWith(imdbPrefix))
                {
                    links.ImdbId = animeId;
                    var imdbMappings = await GetImdbMapping(animeId);
                    mapping = imdbMappings.FirstOrDefault();
                }
                else if (animeId.StartsWith(tmdbPrefix))
                {
                    var tmdbMappings = await GetTmdbMapping(animeId);
                    mapping = tmdbMappings.FirstOrDefault();
                }

                // 2. Enrich with sibling ids from the cross-service mapping —
                //    ??= so the self id (when present) wins over the
                //    mapping's sometimes-stale duplicate.
                if (mapping != null)
                {
                    links.AnilistId ??= mapping.AnilistId;
                    links.MalId ??= mapping.MalId;
                    links.KitsuId ??= mapping.KitsuId;
                    if (string.IsNullOrEmpty(links.ImdbId)
                        && !string.IsNullOrEmpty(mapping.ImdbId)
                        && mapping.ImdbId.StartsWith("tt"))
                        links.ImdbId = mapping.ImdbId;
                    // mapping.Season is the IMDb-side cour pointer.
                    // Anime franchises typically have ONE imdb id with
                    // each cour mapped to a different season number,
                    // so the cour's anilist / mal id → mapping.Season
                    // pair is what tells downstream callers (Torrentio
                    // most importantly) which season of the franchise
                    // we mean. Without this the request silently
                    // resolves to season 1 of the franchise for every
                    // cour.
                    links.ImdbSeason ??= mapping.Season;
                }
            }
            catch
            {
                // Best-effort: a single mapping miss leaves the corresponding
                // fields null. Caller treats an empty result as "no cross-
                // service ids known" — no need for logger plumbing here.
            }

            return links;
        }

        private async Task EnsureMappingsLoadedAsync()
        {
            if (_anilistMapping is not null && DateTime.UtcNow - _lastLoaded < CacheDuration)
                return;

            await _semaphore.WaitAsync();
            try
            {
                if (_anilistMapping is not null && DateTime.UtcNow - _lastLoaded < CacheDuration)
                    return;

                List<AnimeIdMapping> entries = null;
                bool fromNetwork = false;
                try
                {
                    var client = _clientFactory.CreateClient();
                    client.Timeout = FetchTimeout;

                    // Download primary and secondary sources in parallel
                    var fribbTask = client.GetStringAsync(FribbMappingUrl);
                    var offlineTask = DownloadSafeAsync(client, OfflineDbUrl);

                    var fribbJson = await fribbTask;
                    var offlineJson = await offlineTask;

                    entries = DeserializeObject<List<AnimeIdMapping>>(fribbJson) ?? [];

                    // Merge anime-offline-database for broader AniList/Kitsu/MAL coverage
                    if (offlineJson != null)
                        MergeOfflineDatabase(entries, offlineJson);

                    fromNetwork = true;
                }
                catch (Exception ex)
                {
                    // Network load failed (timeout, DNS, 5xx, parse error, …).
                    // If we already have mappings in memory keep them — a stale
                    // 24h+ snapshot is better than no mappings. If memory is
                    // empty, try the on-disk snapshot from a previous run.
                    if (_anilistMapping is not null)
                    {
                        // Stale-but-present: leave memory alone and bail out.
                        // Don't update _lastLoaded so the next request retries.
                        return;
                    }
                    entries = TryReadDiskCache();
                    if (entries is null)
                    {
                        // No memory, no disk — actually propagate the error so
                        // the caller knows the mapping pipeline is unavailable.
                        // Pre-existing callers either catch this (Program.cs's
                        // startup pre-warm) or let it surface as a 500 (which is
                        // honest — without mappings we can't service the call).
                        throw new InvalidOperationException(
                            "Failed to load anime mappings from network and no disk fallback exists.",
                            ex);
                    }
                }

                _anilistMapping = entries
                    .Where(e => e.AnilistId.HasValue)
                    .DistinctBy(e => e.AnilistId!.Value)
                    .ToFrozenDictionary(e => e.AnilistId!.Value, e => e);

                _kitsuMapping = entries
                    .Where(e => e.KitsuId.HasValue)
                    .DistinctBy(e => e.KitsuId!.Value)
                    .ToFrozenDictionary(e => e.KitsuId!.Value, e => e);

                _malMapping = entries
                    .Where(e => e.MalId.HasValue)
                    .DistinctBy(e => e.MalId!.Value)
                    .ToFrozenDictionary(e => e.MalId!.Value, e => e);

                _imdbMapping = new ConcurrentDictionary<string, List<AnimeIdMapping>>(entries
                    .Where(e => !string.IsNullOrEmpty(e.ImdbId))
                    .GroupBy(e => e.ImdbId)
                    .ToDictionary(e => e.Key, e => e.ToList()));

                _tmdbMapping = entries
                    .Where(e => !string.IsNullOrEmpty(e.TmdbId))
                    .GroupBy(e => e.TmdbId)
                    .ToFrozenDictionary(e => e.Key, e => e.ToList());

                // TVDB ids can appear on multiple rows when a TV series has multiple
                // cours/seasons that map to separate AniList entries — same shape as IMDB.
                _tvdbMapping = entries
                    .Where(e => e.TvdbId.HasValue)
                    .GroupBy(e => e.TvdbId!.Value)
                    .ToFrozenDictionary(e => e.Key, e => e.ToList());

                // AniDB ids are 1:1 with anime entries — HAMA (Plex) and AniDB metadata
                // providers (Jellyfin) emit them, so a single-entry index suffices.
                _anidbMapping = entries
                    .Where(e => e.AnidbId.HasValue)
                    .DistinctBy(e => e.AnidbId!.Value)
                    .ToFrozenDictionary(e => e.AnidbId!.Value, e => e);

                _lastLoaded = DateTime.UtcNow;

                // Persist the merged list so the NEXT cold start has
                // something to fall back to if the GitHub fetch fails or
                // hangs. Only writes when this load actually came from the
                // network — re-writing a snapshot loaded from disk would
                // be a no-op at best and a data-race risk at worst.
                if (fromNetwork)
                {
                    _ = Task.Run(() => TryWriteDiskCache(entries));
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // ── Disk cache for the merged mapping list ──────────────────────
        //
        // Saved to ANISYNC_DATA_DIR (Fly's persistent volume) so subsequent
        // cold starts can boot with the last-known-good mapping table when
        // raw.githubusercontent.com is unreachable. The on-disk format is
        // just the JSON serialisation of the in-memory entries list — same
        // shape Fribb's master file uses, so we can deserialize via the
        // same path as the network response.

        private List<AnimeIdMapping> TryReadDiskCache()
        {
            try
            {
                if (!File.Exists(_diskCachePath)) return null;
                var json = File.ReadAllText(_diskCachePath);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return DeserializeObject<List<AnimeIdMapping>>(json);
            }
            catch
            {
                // Best-effort fallback; a corrupt cache shouldn't crash
                // the service. The next successful network load will
                // overwrite it.
                return null;
            }
        }

        private void TryWriteDiskCache(List<AnimeIdMapping> entries)
        {
            try
            {
                var json = SerializeObject(entries);
                // Atomic-ish write via temp file + rename so a crash mid-
                // write can't leave a half-written JSON behind that
                // TryReadDiskCache would later choke on.
                var tmp = _diskCachePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _diskCachePath, overwrite: true);
            }
            catch
            {
                // Best-effort persistence — losing the write is OK,
                // it just means the next cold start has no fallback.
            }
        }

        /// <summary>
        /// If the mapping has a TMDB ID but no IMDb ID, resolves it via the TMDB external_ids API.
        /// Results are cached so each TMDB ID is fetched at most once.
        /// </summary>
        private async Task TryEnrichImdbAsync(AnimeIdMapping mapping)
        {
            if (mapping == null || !string.IsNullOrEmpty(mapping.ImdbId) || string.IsNullOrEmpty(mapping.TmdbId))
                return;

            if (_tmdbToImdbCache.TryGetValue(mapping.TmdbId, out var cachedImdb))
            {
                mapping.ImdbId = cachedImdb;
                return;
            }

            var imdbId = await FetchImdbFromTmdbAsync(mapping.TmdbId);
            _tmdbToImdbCache.TryAdd(mapping.TmdbId, imdbId ?? "");

            if (!string.IsNullOrEmpty(imdbId))
            {
                var mappings = await GetTmdbMapping(mapping.TmdbId);

                mappings.ForEach(f => f.ImdbId = imdbId);

                var imdbMappings = await GetImdbMapping(imdbId);

                mappings.AddRange(imdbMappings.ExceptBy(
                    mappings.Select(w => (w.AnilistId, w.KitsuId, w.MalId)),
                    w => (w.AnilistId, w.KitsuId, w.MalId)));

                mapping.ImdbId = imdbId;
                _imdbMapping.AddOrUpdate(imdbId,
                    _ => mappings,
                    (_, list) => mappings);
            }
        }

        /// <summary>
        /// Calls TMDB /external_ids for TV first, then movie, to resolve an IMDb ID.
        /// </summary>
        private async Task<string> FetchImdbFromTmdbAsync(string tmdbId)
        {
            var token = _configuration["TmdbReadToken"];
            if (string.IsNullOrEmpty(token))
                return null;

            try
            {
                var client = _clientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                // Most anime are TV series; try that first
                var response = await client.GetAsync($"{TmdbApiBase}/tv/{tmdbId}/external_ids");
                if (response.IsSuccessStatusCode)
                {
                    var result = DeserializeObject<dynamic>(await response.Content.ReadAsStringAsync());
                    var imdb = (string)result?.imdb_id;
                    if (!string.IsNullOrEmpty(imdb))
                        return imdb;
                }

                // Fallback to movie
                response = await client.GetAsync($"{TmdbApiBase}/movie/{tmdbId}/external_ids");
                if (response.IsSuccessStatusCode)
                {
                    var result = DeserializeObject<dynamic>(await response.Content.ReadAsStringAsync());
                    var imdb = (string)result?.imdb_id;
                    if (!string.IsNullOrEmpty(imdb))
                        return imdb;
                }
            }
            catch
            {
                // TMDB enrichment is best-effort
            }

            return null;
        }

        private static async Task<string> DownloadSafeAsync(HttpClient client, string url)
        {
            try
            {
                return await client.GetStringAsync(url);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Merges anime-offline-database entries into the Fribb mapping list.
        /// Fills missing AniList, Kitsu, and MAL IDs on existing entries and adds new entries.
        /// </summary>
        private static void MergeOfflineDatabase(List<AnimeIdMapping> entries, string offlineJson)
        {
            var offlineDb = DeserializeObject<OfflineDbRoot>(offlineJson);
            if (offlineDb?.Data == null) return;

            var byAnilist = entries
                .Where(e => e.AnilistId.HasValue)
                .GroupBy(e => e.AnilistId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            var byKitsu = entries
                .Where(e => e.KitsuId.HasValue)
                .GroupBy(e => e.KitsuId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            var byMal = entries
                .Where(e => e.MalId.HasValue)
                .GroupBy(e => e.MalId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var offlineEntry in offlineDb.Data)
            {
                if (offlineEntry.Sources == null) continue;

                int? anilistId = null, kitsuId = null, malId = null;
                foreach (var source in offlineEntry.Sources)
                {
                    if (TryExtractId(source, "https://anilist.co/anime/", out var aid))
                        anilistId = aid;
                    else if (TryExtractId(source, "https://kitsu.app/anime/", out var kid))
                        kitsuId = kid;
                    else if (TryExtractId(source, "https://kitsu.io/anime/", out var kid2))
                        kitsuId = kid2;
                    else if (TryExtractId(source, "https://myanimelist.net/anime/", out var mid))
                        malId = mid;
                }

                if (!anilistId.HasValue && !kitsuId.HasValue && !malId.HasValue)
                    continue;

                // Find existing entry by any matching ID
                AnimeIdMapping existing = null;
                if (anilistId.HasValue) byAnilist.TryGetValue(anilistId.Value, out existing);
                if (existing == null && kitsuId.HasValue) byKitsu.TryGetValue(kitsuId.Value, out existing);
                if (existing == null && malId.HasValue) byMal.TryGetValue(malId.Value, out existing);

                if (existing != null)
                {
                    // Fill gaps in existing Fribb entry
                    if (!existing.AnilistId.HasValue && anilistId.HasValue)
                    {
                        existing.AnilistId = anilistId;
                        byAnilist.TryAdd(anilistId.Value, existing);
                    }
                    if (!existing.KitsuId.HasValue && kitsuId.HasValue)
                    {
                        existing.KitsuId = kitsuId;
                        byKitsu.TryAdd(kitsuId.Value, existing);
                    }
                    if (!existing.MalId.HasValue && malId.HasValue)
                    {
                        existing.MalId = malId;
                        byMal.TryAdd(malId.Value, existing);
                    }
                }
                else if (anilistId.HasValue || kitsuId.HasValue)
                {
                    var newEntry = new AnimeIdMapping
                    {
                        AnilistId = anilistId,
                        KitsuId = kitsuId,
                        MalId = malId
                    };
                    entries.Add(newEntry);
                    if (anilistId.HasValue) byAnilist.TryAdd(anilistId.Value, newEntry);
                    if (kitsuId.HasValue) byKitsu.TryAdd(kitsuId.Value, newEntry);
                    if (malId.HasValue) byMal.TryAdd(malId.Value, newEntry);
                }
            }
        }

        private static bool TryExtractId(string url, string prefix, out int id)
        {
            id = 0;
            if (!url.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var segment = url[prefix.Length..].TrimEnd('/');
            return int.TryParse(segment, out id);
        }

        public async Task<string> GetIdByService(string animeId, AnimeService service, int? season = null)
        {
            if (string.IsNullOrEmpty(animeId))
                return null;

            if (animeId.StartsWith(anilistPrefix))
            {
                if (service == AnimeService.Anilist)
                    return animeId.Replace(anilistPrefix, "");

                var mapping = await GetAnilistMapping(animeId);
                return PickServiceId(mapping, service);
            }
            else if (animeId.StartsWith(kitsuPrefix))
            {
                // Same-service short-circuit: the id already names the Kitsu anime, no
                // mapping lookup needed. Without this, a Kitsu anime that isn't in our
                // local mapping cache (newer entries, gaps in the upstream data) would
                // resolve to null and the meta endpoint would return no data.
                if (service == AnimeService.Kitsu)
                    return animeId.Replace(kitsuPrefix, "");

                var mapping = await GetKitsuMapping(animeId);
                return PickServiceId(mapping, service);
            }
            else if (animeId.StartsWith(imdbPrefix))
            {
                var mapping = await GetImdbMapping(animeId, season);
                return PickServiceId(mapping?.FirstOrDefault(), service);
            }
            else if (animeId.StartsWith(tmdbPrefix))
            {
                var mapping = await GetTmdbMapping(animeId, season);
                return PickServiceId(mapping?.FirstOrDefault(), service);
            }
            else if (animeId.StartsWith(malPrefix))
            {
                if (service == AnimeService.MyAnimeList)
                    return animeId.Replace(malPrefix, "");

                var mapping = await GetMalMapping(animeId);
                return PickServiceId(mapping, service);
            }
            else if (animeId.StartsWith(tvdbPrefix))
            {
                await EnsureMappingsLoadedAsync();
                if (!int.TryParse(animeId[tvdbPrefix.Length..], out var tvdb)) return null;
                if (!_tvdbMapping.TryGetValue(tvdb, out var mappings)) return null;

                var mapping = mappings
                    .Where(m => !season.HasValue || m.Season == season || !m.Season.HasValue)
                    .FirstOrDefault();
                return PickServiceId(mapping, service);
            }
            else if (animeId.StartsWith(anidbPrefix))
            {
                await EnsureMappingsLoadedAsync();
                if (!int.TryParse(animeId[anidbPrefix.Length..], out var anidb)) return null;
                if (!_anidbMapping.TryGetValue(anidb, out var mapping)) return null;
                return PickServiceId(mapping, service);
            }

            return animeId;
        }

        /// <summary>
        /// Walks a list of external IDs (e.g. from a Plex/Jellyfin webhook payload) in priority
        /// order and returns the first one that resolves to <paramref name="service"/>'s id.
        /// AniDB → IMDB → TMDB → TVDB is the priority because AniDB and IMDB are 1:1 with the
        /// tracker entry, while TVDB/TMDB sometimes need season disambiguation.
        ///
        /// Each tuple is <c>(prefix, raw id)</c> where <c>prefix</c> is one of the existing
        /// id-prefix constants (<see cref="anidbPrefix"/>, <see cref="imdbPrefix"/>, etc.).
        /// Callers don't have to pre-filter unknown prefixes — anything <see cref="GetIdByService"/>
        /// doesn't recognise resolves to null and the loop tries the next id.
        /// </summary>
        public async Task<string> ResolveExternalAsync(IEnumerable<(string prefix, string id)> externalIds,
            AnimeService service, int? season = null)
        {
            if (externalIds == null) return null;

            var ranked = externalIds
                .Where(t => !string.IsNullOrEmpty(t.id))
                .OrderBy(t => t.prefix switch
                {
                    var p when p == anidbPrefix => 0,
                    var p when p == imdbPrefix => 1,
                    var p when p == tmdbPrefix => 2,
                    var p when p == tvdbPrefix => 3,
                    _ => 99,
                });

            foreach (var (prefix, id) in ranked)
            {
                // imdbPrefix is "tt" so the id already carries it; everything else needs prefixing.
                var prefixed = prefix == imdbPrefix && id.StartsWith(imdbPrefix) ? id : prefix + id;
                var resolved = await GetIdByService(prefixed, service, season);
                if (!string.IsNullOrEmpty(resolved)) return resolved;
            }
            return null;
        }

        private static string PickServiceId(AnimeIdMapping mapping, AnimeService service) => service switch
        {
            AnimeService.Anilist => mapping?.AnilistId?.ToString(),
            AnimeService.Kitsu => mapping?.KitsuId?.ToString(),
            AnimeService.MyAnimeList => mapping?.MalId?.ToString(),
            _ => null,
        };

        /// <summary>
        /// Returns distinct season numbers for all mapping entries that share the same base anime ID.
        /// </summary>
        public async Task<List<int>> GetSeasonsAsync(string animeId)
        {
            if (string.IsNullOrEmpty(animeId)) return [];

            List<AnimeIdMapping> entries;

            if (animeId.StartsWith(imdbPrefix))
                entries = await GetImdbMapping(animeId);
            else if (animeId.StartsWith(tmdbPrefix))
                entries = await GetTmdbMapping(animeId);
            else
                return [];

            return entries
                .Where(e => e.Season.HasValue)
                .Select(e => e.Season!.Value)
                .Distinct()
                .Order()
                .ToList();
        }

        private sealed class OfflineDbRoot
        {
            public List<OfflineDbEntry> Data { get; set; }
        }

        private sealed class OfflineDbEntry
        {
            public List<string> Sources { get; set; }
        }
    }
}
