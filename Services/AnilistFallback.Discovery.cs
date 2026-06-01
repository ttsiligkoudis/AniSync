using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace AnimeList.Services
{
    /// <summary>
    /// <see cref="AnilistFallback"/> partial: catalog / airing-schedule / season-stats methods.
    /// These all hit AniList's anonymous query surface to surface "what's airing" and
    /// "what's new this season" shelves on the dashboard + Discover surfaces.
    /// </summary>
    public partial class AnilistFallback
    {
        public async Task<List<Meta>> GetAiringScheduleAsync(AnimeService translateTo, string skip = null, string genre = null, bool hideAdult = false, bool groupSeasons = true)
        {
            // Genre branch: AniList's airingSchedules query has no genre
            // filter, so when the caller wants to slice "airing" by genre we
            // swap to the Media(status: RELEASING, genre: $genre) shape. Same
            // user-facing intent — "show me what's airing in Action" — just
            // sourced from currently-airing anime rather than the upcoming-
            // episode schedule. Results lose per-episode air dates because
            // there's no schedule attached to a status-based fetch; the cards
            // still carry the show's full metadata.
            if (!string.IsNullOrEmpty(genre))
            {
                return await GetCurrentlyAiringByGenreAsync(translateTo, skip, genre, hideAdult, groupSeasons);
            }

            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;

            // Request isAdult on the embedded media so we can drop hentai
            // entries client-side when the viewer has Show 18+ disabled.
            // AniList's airingSchedules query itself takes no isAdult arg —
            // unlike Media() — so post-filter is the only option.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($page: Int, $perPage: Int) {
                        Page(page: $page, perPage: $perPage) {
                            airingSchedules(notYetAired: true, sort: TIME) {
                                airingAt
                                episode
                                media {
                                    id
                                    format
                                    isAdult
                                    title { english romaji }
                                    coverImage { large }
                                    description
                                }
                            }
                        }
                    }",
                variables = new { page, perPage = PageSize }
            });

            var data = await PostJsonAsync(requestBody);
            var schedules = data?.Page?.airingSchedules;
            if (schedules == null) return [];

            await _mappingService.EnsureLoadedAsync();

            var seen = new HashSet<string>();
            var result = new List<Meta>();
            foreach (var sched in schedules)
            {
                var media = sched.media;
                if (media == null) continue;

                var anilistId = (int?)media.id;
                if (!anilistId.HasValue) continue;

                if (hideAdult && (bool?)media.isAdult == true) continue;

                var externalId = await ResolveStremioIdAsync(anilistId.Value, translateTo, groupSeasons);
                if (!seen.Add(externalId)) continue;

                var name = string.IsNullOrEmpty((string)media.title.english)
                    ? (string)media.title.romaji
                    : (string)media.title.english;

                var airingAt = (long?)sched.airingAt;
                var episode = (int?)sched.episode;
                var description = BuildAiringDescription((string)media.description, episode, airingAt);

                result.Add(new Meta(description)
                {
                    id = externalId,
                    type = IsMovieFormat((string)media.format) ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = name,
                    poster = (string)media.coverImage?.large,
                });
            }

            return result;
        }

        // "Airing + genre" fallback path. Uses Media(status: RELEASING, genre)
        // because airingSchedules can't be sliced per-genre. Sort by popularity
        // so the most-watched currently-airing shows in the genre lead. Media()
        // accepts an isAdult arg directly so the 18+ gate is server-side here,
        // unlike the schedule branch which has to post-filter.
        private async Task<List<Meta>> GetCurrentlyAiringByGenreAsync(AnimeService translateTo, string skip, string genre, bool hideAdult, bool groupSeasons)
        {
            var page = int.TryParse(skip, out var skipInt) ? (skipInt / PageSize) + 1 : 1;

            var adultArg = hideAdult ? ", isAdult: false" : string.Empty;
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($page: Int, $perPage: Int, $genre: String) {
                        Page(page: $page, perPage: $perPage) {
                            media(type: ANIME, status: RELEASING, genre: $genre, sort: POPULARITY_DESC" + adultArg + @") {
                                id
                                format
                                title { english romaji }
                                coverImage { large }
                                description
                            }
                        }
                    }",
                variables = new { page, perPage = PageSize, genre }
            });

            var data = await PostJsonAsync(requestBody);
            var mediaArr = data?.Page?.media;
            if (mediaArr == null) return [];

            await _mappingService.EnsureLoadedAsync();

            var seen = new HashSet<string>();
            var result = new List<Meta>();
            foreach (var media in mediaArr)
            {
                var anilistId = (int?)media.id;
                if (!anilistId.HasValue) continue;

                var externalId = await ResolveStremioIdAsync(anilistId.Value, translateTo, groupSeasons);
                if (!seen.Add(externalId)) continue;

                var name = string.IsNullOrEmpty((string)media.title?.english)
                    ? (string)media.title?.romaji
                    : (string)media.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                result.Add(new Meta((string)media.description)
                {
                    id = externalId,
                    type = IsMovieFormat((string)media.format) ? MetaType.movie.ToString() : MetaType.series.ToString(),
                    name = name,
                    poster = (string)media.coverImage?.large,
                });
            }

            return result;
        }

        public async Task<(int currentlyAiring, int newThisSeason, int totalThisSeason)> GetSeasonStatsAsync()
        {
            // Cache key includes season + year so the entry naturally
            // invalidates when AniList moves to the next season — the
            // OLD entry just sits dead in cache until eviction.
            // AnilistFallback is registered as Scoped, but IMemoryCache is
            // the singleton so the cache survives across requests despite
            // this service's per-request lifetime.
            var (season, year) = GetSeasonAndYear(SeasonCurrent);
            var cacheKey = $"anilist:season-stats:{season}:{year}";

            if (_cache.TryGetValue<(int, int, int)>(cacheKey, out var cached))
            {
                return cached;
            }

            // Earlier attempts derived the three counts from
            // `Page.pageInfo.total` of three separately-filtered Page
            // queries, but AniList's pageInfo.total reliably collapses to
            // the 5000 hard-cap whenever filter args are present (even on
            // a single-Page document). Switching to a list-walk: pull the
            // season's whole media list (~50–200 entries), each carrying
            // its `status`, and bucket client-side. perPage:50 means
            // 1–4 page fetches per call, gated by the 24h cache.
            try
            {
                var stats = await FetchSeasonStatsAsync(season, year);
                if (stats.totalThisSeason > 0)
                {
                    // Only cache successful results so a transient blip
                    // (sandbox blocks GraphQL / rate-limit / 5xx) doesn't
                    // lock in zeros for 24 hours.
                    _cache.Set(cacheKey, stats, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = SeasonStatsCacheDuration,
                    });
                }
                return stats;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetSeasonStatsAsync failed: {ex.Message}");
                return (0, 0, 0);
            }
        }

        private async Task<(int currentlyAiring, int newThisSeason, int totalThisSeason)> FetchSeasonStatsAsync(string season, int year)
        {
            int total = 0;
            int airing = 0;
            int newThis = 0;
            int page = 1;

            while (true)
            {
                var requestBody = SerializeObject(new
                {
                    query = @"
                        query ($page: Int, $season: MediaSeason, $seasonYear: Int) {
                            Page(page: $page, perPage: 50) {
                                pageInfo { hasNextPage }
                                media(season: $season, seasonYear: $seasonYear, type: ANIME, isAdult: false) {
                                    status
                                }
                            }
                        }",
                    variables = new { page, season, seasonYear = year }
                });

                var data = await PostJsonAsync(requestBody);
                var mediaArr = data?.Page?.media;
                if (mediaArr == null) return (0, 0, 0);

                foreach (var m in mediaArr)
                {
                    total++;
                    var status = (string)m.status;
                    // RELEASING counts as both "airing now" and "new this season".
                    // NOT_YET_RELEASED counts only as "new this season" (premieres
                    // later in the calendar season). FINISHED/CANCELLED/HIATUS
                    // are still in the season's catalog but neither airing nor
                    // newly-arriving — only counted into the total.
                    if (status == "RELEASING") { airing++; newThis++; }
                    else if (status == "NOT_YET_RELEASED") { newThis++; }
                }

                var hasNext = (bool?)data?.Page?.pageInfo?.hasNextPage ?? false;
                if (!hasNext) break;
                page++;
                if (page > 20) break; // safety: ~1000 entries is well past any real season
            }

            return (airing, newThis, total);
        }

        public async Task<List<Meta>> GetNewEpisodesTodayAsync(
            AnimeService translateTo = AnimeService.Anilist,
            int tzOffsetMinutes = 0,
            bool groupSeasons = false)
        {
            // Bucket "today" against the viewer's local calendar day rather
            // than UTC. tzOffsetMinutes follows JS Date.getTimezoneOffset()
            // convention: minutes WEST of UTC (UTC+3 = -180, UTC-5 = +300).
            // Negate it to get the TimeSpan offset DateTimeOffset wants.
            // A UTC+3 viewer at local 02:16 (their day 16) sees the window
            // [16 00:00 local, 17 00:00 local) which translates to UTC
            // [15 21:00, 16 21:00) — yesterday-UTC's late evening through
            // today-UTC's afternoon. Without this, a rolling-now-±18h
            // window over-included yesterday's afternoon airings (still
            // within ±18h of UTC-midnight) and tomorrow's morning airings.
            var tz = TimeSpan.FromMinutes(-tzOffsetMinutes);
            var localNow = DateTimeOffset.UtcNow.ToOffset(tz);
            var localToday = new DateTimeOffset(localNow.Date, tz);
            var startUnix = localToday.ToUnixTimeSeconds();
            var endUnix = localToday.AddDays(1).ToUnixTimeSeconds();

            // Cache key includes the local-day + timezone offset so
            // viewers in different timezones get their own cached
            // bucket and each rotates at its own midnight. The hour
            // suffix limits per-tz cache lifetime to roughly an hour
            // so an airing-schedule shift propagates quickly.
            var cacheKey = $"anilist:new-episodes-today:{localToday:yyyyMMdd}:tz{tzOffsetMinutes}:{localNow:HH}";

            if (_cache.TryGetValue<List<Meta>>(cacheKey, out var cached) && cached != null)
            {
                // Cache stores anilist:N ids (one entry per hour, shared
                // across every viewer). Translate per-call into the
                // requesting service's native id space so a MAL/Kitsu
                // primary's clicks resolve directly. CloneMetas first so
                // the cached list itself doesn't get mutated.
                return await TranslateMetaIdsAsync(CloneMetas(cached), translateTo, groupSeasons);
            }

            try
            {
                var requestBody = SerializeObject(new
                {
                    query = @"
                        query ($startUnix: Int, $endUnix: Int, $page: Int) {
                            Page(page: $page, perPage: 50) {
                                pageInfo { hasNextPage }
                                airingSchedules(airingAt_greater: $startUnix, airingAt_lesser: $endUnix, sort: TIME) {
                                    airingAt
                                    episode
                                    media {
                                        id
                                        format
                                        episodes
                                        averageScore
                                        seasonYear
                                        isAdult
                                        title { english romaji }
                                        coverImage { large }
                                    }
                                }
                            }
                        }",
                    variables = new { startUnix, endUnix, page = 1 }
                });

                var data = await PostJsonAsync(requestBody);
                var schedules = data?.Page?.airingSchedules;
                if (schedules == null) return [];

                // Multiple schedules can hit on the same anime in one day
                // (cours overlapping, special + main, etc.) — dedupe by
                // anilist id and keep the earliest airing time on the day.
                var seen = new HashSet<int>();
                var result = new List<Meta>();
                foreach (var sched in schedules)
                {
                    var media = sched.media;
                    if (media == null) continue;

                    // Dashboard shelf cache is shared across every viewer, so
                    // we can't honor a per-user showAdultContent toggle here.
                    // Match the safer default — explicit 18+ entries never
                    // surface on the dashboard for anyone. Users who opt in
                    // can still find them via Discover / search where the
                    // toggle is per-user.
                    if ((bool?)media.isAdult == true) continue;

                    var anilistId = (int?)media.id;
                    if (!anilistId.HasValue || !seen.Add(anilistId.Value)) continue;

                    var name = string.IsNullOrEmpty((string)media.title?.english)
                        ? (string)media.title?.romaji
                        : (string)media.title?.english;
                    if (string.IsNullOrEmpty(name)) continue;

                    result.Add(new Meta
                    {
                        // Stay in anilist:N space so AnimeController.Detail
                        // can resolve to the user's primary on click — same
                        // pattern as the other dashboard shelves.
                        id = $"{anilistPrefix}{anilistId.Value}",
                        name = name,
                        poster = (string)media.coverImage?.large,
                        type = IsMovieFormat((string)media.format)
                            ? MetaType.movie.ToString()
                            : MetaType.series.ToString(),
                        score = media.averageScore != null
                            ? Math.Round((double)media.averageScore / 10, 1)
                            : (double?)null,
                        episodes = (int?)media.episodes,
                        year = (int?)media.seasonYear,
                        format = NormalizeFormat((string)media.format),
                        airingEpisode = (int?)sched.episode,
                        airingAt = (long?)sched.airingAt,
                    });
                }

                if (result.Count > 0)
                {
                    // Final ascending sort by airingAt — the GraphQL `sort: TIME`
                    // already returns ascending, but make the order an explicit
                    // post-condition so a future upstream change can't quietly
                    // reorder the dashboard shelf.
                    result = result.OrderBy(m => m.airingAt ?? long.MaxValue).ToList();

                    // Hold the snapshot for one hour. With the rolling
                    // window the dashboard never shows episodes more than
                    // LookbackHours old — caching longer would let the
                    // user-facing "today" drift past that threshold
                    // before the next refresh.
                    _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                    });
                }
                // Same translate-on-return contract as the cache-hit path so
                // the caller's primary determines the id-space regardless of
                // whether this call populated or read the cache.
                return await TranslateMetaIdsAsync(CloneMetas(result), translateTo, groupSeasons);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetNewEpisodesTodayAsync failed: {ex.Message}");
                return [];
            }
        }

        public async Task<List<UpcomingEpisode>> GetUpcomingEpisodesAsync(long startUnix, long endUnix)
        {
            // Same airingSchedules shape as GetNewEpisodesTodayAsync above, but
            // parameterised on an arbitrary window with no caching — the caller's
            // cron tick is the cadence (every 5 min). Pulls up to 100 entries per
            // window which comfortably covers a 24h horizon (~50-80 airings/day
            // peak Saturday); if the window grows we'd need to paginate.
            try
            {
                var requestBody = SerializeObject(new
                {
                    query = @"
                        query ($startUnix: Int, $endUnix: Int, $page: Int) {
                            Page(page: $page, perPage: 100) {
                                airingSchedules(airingAt_greater: $startUnix, airingAt_lesser: $endUnix, sort: TIME) {
                                    airingAt
                                    episode
                                    media {
                                        id
                                        title { english romaji }
                                        coverImage { large }
                                    }
                                }
                            }
                        }",
                    variables = new { startUnix, endUnix, page = 1 }
                });

                var data = await PostJsonAsync(requestBody);
                var schedules = data?.Page?.airingSchedules;
                if (schedules == null) return [];

                var result = new List<UpcomingEpisode>();
                foreach (var sched in schedules)
                {
                    var media = sched.media;
                    if (media == null) continue;

                    var anilistId = (int?)media.id;
                    var episode = (int?)sched.episode;
                    var airingAt = (long?)sched.airingAt;
                    if (!anilistId.HasValue || !episode.HasValue || !airingAt.HasValue) continue;

                    var name = string.IsNullOrEmpty((string)media.title?.english)
                        ? (string)media.title?.romaji
                        : (string)media.title?.english;
                    if (string.IsNullOrEmpty(name)) continue;

                    result.Add(new UpcomingEpisode(
                        AnilistId: anilistId.Value,
                        Title: name,
                        Episode: episode.Value,
                        AiringAt: airingAt.Value,
                        CoverImage: (string)media.coverImage?.large));
                }
                return result;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetUpcomingEpisodesAsync failed: {ex.Message}");
                return [];
            }
        }

        public async Task<List<UpcomingEpisode>> GetAiringForMediaAsync(
            IReadOnlyCollection<int> anilistIds, long startUnix, long endUnix)
        {
            var result = new List<UpcomingEpisode>();
            if (anilistIds == null || anilistIds.Count == 0) return result;

            // Query each media's own airingSchedule (id_in batches of 50, AniList's
            // page cap). The nested connection can't be range-filtered server-side,
            // so we pull up to 50 schedule nodes per show and window-filter here —
            // ample for seasonal shows (a long-runner past 50 aired episodes is the
            // rare exception). Per-chunk try/catch so one bad page doesn't sink the
            // whole month.
            var distinct = anilistIds.Distinct().ToList();
            const int chunkSize = 50;
            for (var offset = 0; offset < distinct.Count; offset += chunkSize)
            {
                var chunk = distinct.Skip(offset).Take(chunkSize).ToArray();
                try
                {
                    var requestBody = SerializeObject(new
                    {
                        query = @"
                            query ($ids: [Int], $page: Int) {
                                Page(page: $page, perPage: 50) {
                                    media(id_in: $ids, type: ANIME) {
                                        id
                                        title { english romaji }
                                        coverImage { large }
                                        airingSchedule(perPage: 50) {
                                            nodes { episode airingAt }
                                        }
                                    }
                                }
                            }",
                        variables = new { ids = chunk, page = 1 }
                    });

                    var data = await PostJsonAsync(requestBody);
                    var mediaList = data?.Page?.media;
                    if (mediaList == null) continue;

                    foreach (var media in mediaList)
                    {
                        var anilistId = (int?)media.id;
                        if (!anilistId.HasValue) continue;

                        var name = string.IsNullOrEmpty((string)media.title?.english)
                            ? (string)media.title?.romaji
                            : (string)media.title?.english;
                        if (string.IsNullOrEmpty(name)) continue;

                        var cover = (string)media.coverImage?.large;
                        var nodes = media.airingSchedule?.nodes;
                        if (nodes == null) continue;

                        foreach (var node in nodes)
                        {
                            var episode = (int?)node.episode;
                            var airingAt = (long?)node.airingAt;
                            if (!episode.HasValue || !airingAt.HasValue) continue;
                            if (airingAt.Value < startUnix || airingAt.Value > endUnix) continue;

                            result.Add(new UpcomingEpisode(
                                AnilistId: anilistId.Value,
                                Title: name,
                                Episode: episode.Value,
                                AiringAt: airingAt.Value,
                                CoverImage: cover));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AnilistFallback] GetAiringForMediaAsync chunk failed: {ex.Message}");
                }
            }
            return result;
        }

        // 1h TTL is the same horizon the notification scheduler treats as
        // fresh enough to act on — beyond that, the daily refresh + per-
        // minute tick combo will have re-pulled the global schedule anyway.
        // Caching per anime keeps a hot detail page from hammering AniList
        // on repeated reloads (e.g. an anime in active discussion on a forum
        // funnels traffic at one id) without ever drifting past the window
        // the notifier already considers actionable.
        private static readonly TimeSpan AiringScheduleCacheDuration = TimeSpan.FromHours(1);

        public async Task<Dictionary<int, long>> GetAiringScheduleByAnilistIdAsync(int anilistId)
        {
            if (anilistId <= 0) return [];

            var cacheKey = $"anilist:airingSchedule:{anilistId}";
            if (_cache.TryGetValue<Dictionary<int, long>>(cacheKey, out var cached) && cached != null)
                return cached;

            // Single Media.airingSchedule fetch with a generous perPage so a
            // standard 12/24-episode cour resolves in one round-trip. AniList
            // caps perPage at 50; the rare long-form continuous run (Naruto-
            // style) would need pagination, but those are vanishingly few in
            // the Cinemeta-mapped catalog we hit this for.
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            airingSchedule(perPage: 50) {
                                nodes { episode airingAt }
                            }
                        }
                    }",
                variables = new { id = anilistId }
            });

            try
            {
                var data = await PostJsonAsync(requestBody);
                var nodes = data?.Media?.airingSchedule?.nodes;
                var result = new Dictionary<int, long>();
                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        var episode = (int?)node.episode;
                        var airingAt = (long?)node.airingAt;
                        if (!episode.HasValue || !airingAt.HasValue) continue;
                        // Earliest wins when AniList returns duplicate rows
                        // for the same episode (split-cour shows occasionally
                        // do this around airing-day shifts) so the click gate
                        // unlocks at the first real airing, not a re-air.
                        if (result.TryGetValue(episode.Value, out var existing) && existing <= airingAt.Value)
                            continue;
                        result[episode.Value] = airingAt.Value;
                    }
                }
                _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = AiringScheduleCacheDuration,
                });
                return result;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetAiringScheduleByAnilistIdAsync failed: {ex.Message}");
                return [];
            }
        }

    }
}
