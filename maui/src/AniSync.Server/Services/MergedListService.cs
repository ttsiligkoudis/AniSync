using AnimeList.Models;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services
{
    /// <summary>
    /// Fans a user's list query out across the primary + every healthy linked
    /// secondary and merges the results. See <see cref="IMergedListService"/>
    /// for the contract; the dedup rules live in
    /// <see cref="GetMergedListAsync"/>.
    /// </summary>
    public class MergedListService : IMergedListService
    {
        private readonly IAnilistService _anilistService;
        private readonly IKitsuService _kitsuService;
        private readonly IMalService _malService;
        private readonly ITraktService _traktService;
        private readonly IConfigStore _configStore;
        private readonly IAnimeMappingService _mappingService;
        private readonly ILogger<MergedListService> _logger;

        public MergedListService(
            IAnilistService anilistService,
            IKitsuService kitsuService,
            IMalService malService,
            ITraktService traktService,
            IConfigStore configStore,
            IAnimeMappingService mappingService,
            ILogger<MergedListService> logger)
        {
            _anilistService = anilistService;
            _kitsuService = kitsuService;
            _malService = malService;
            _traktService = traktService;
            _configStore = configStore;
            _mappingService = mappingService;
            _logger = logger;
        }

        /// <summary>
        /// Fetches the user's list for the requested status across the primary
        /// AND every healthy linked secondary, in parallel, then merges into a
        /// single deduped list. Same anime fetched from multiple providers
        /// collapses to one card via the cross-service mapping table — the
        /// dedup key is the primary's id when the mapping has one (and the
        /// meta.id is rewritten to that primary id so the card links to the
        /// canonical detail URL), falling back to a canonical AniList id for
        /// linked-only anime, and finally the raw service id when even AniList
        /// has no mapping. A title-similarity safety net catches duplicates
        /// the mapping can't link. Per-provider failures degrade gracefully:
        /// a logged warning and an empty contribution from that provider, so
        /// an AniList outage doesn't blank the user's MAL + Kitsu cards.
        /// </summary>
        public async Task<List<Meta>> GetMergedListAsync(
            TokenData primary, string uid, ListType listType,
            string genre = null, bool groupSeasons = false,
            bool hideUnreleased = false, bool hideAdult = false)
        {
            if (primary == null) return [];

            // Trakt is a movies/series tracker with no anime id-space: its anime
            // are tracked as imdb-keyed video entries. So when the primary is
            // Trakt the whole merge runs in the IMDb id-space — exactly like the
            // group-anime-seasons setting — which lets the Trakt anime list
            // (imdb ids) and every linked AniList/MAL/Kitsu list (grouped onto
            // their imdb ids) dedup against each other on a shared key.
            if (primary.anime_service == AnimeService.Trakt) groupSeasons = true;

            // Active linked tokens — same gate SyncService uses (skip
            // NeedsReauth + missing access_token entries so we don't fan out
            // into a guaranteed-failure call).
            var linked = string.IsNullOrEmpty(uid)
                ? new List<LinkedToken>()
                : await _configStore.GetLinkedTokensAsync(uid);
            var sources = new List<(TokenData Token, AnimeService Service)>
            {
                (primary, primary.anime_service),
            };
            foreach (var l in linked)
            {
                if (l.NeedsReauth || l.TokenData == null) continue;
                if (string.IsNullOrEmpty(l.TokenData.access_token)) continue;
                if (l.Service == primary.anime_service) continue;
                // Trakt has no anime list of its own — it tracks anime as TV series, keyed per-status.
                // When the primary IS a real anime tracker (AniList/MAL/Kitsu) it's authoritative for
                // anime watch-status, so don't fold Trakt's anime copies into the anime merge: a title
                // the user COMPLETED on the anime side but left "watching" on Trakt would otherwise
                // leak back in (it's only in Trakt's Current list, so there's no primary entry to win
                // the dedup) and reappear in Continue-watching / Library, ignoring the anime status —
                // and re-saving on the anime side can't shift it because it's the Trakt copy. (A
                // Trakt-PRIMARY user has no separate anime list, so Trakt still contributes there.)
                if (l.Service == AnimeService.Trakt && primary.anime_service != AnimeService.Trakt) continue;
                sources.Add((l.TokenData, l.Service));
            }

            // Per-provider fetch wrapped so one upstream's failure doesn't
            // poison the whole Task.WhenAll. Each entry returns an empty list
            // on exception and the merge step just skips it.
            async Task<List<Meta>> SafeFetch(TokenData token, AnimeService service)
            {
                try
                {
                    return service switch
                    {
                        AnimeService.Anilist     => await _anilistService.GetAnimeListAsync(token, listType, genre: genre, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                        AnimeService.MyAnimeList => await _malService.GetAnimeListAsync(token, listType, genre: genre, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                        AnimeService.Trakt       => await FetchTraktAnimeListAsync(uid, listType),
                        _                        => await _kitsuService.GetAnimeListAsync(token, listType, genre: genre, groupSeasons: groupSeasons, hideUnreleased: hideUnreleased, hideAdult: hideAdult),
                    } ?? new List<Meta>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Merged-list multi-provider fetch failed for {Service}.", service);
                    return new List<Meta>();
                }
            }

            var results = await Task.WhenAll(sources.Select(s => SafeFetch(s.Token, s.Service)));

            // Primary's batch is index 0 so its entries land first; collisions
            // from linked providers get skipped via the seen-set, which means
            // primary wins on overlap (status / progress / score the user has
            // on their primary list shows on the card, not the linked
            // provider's potentially stale copy).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Title-based safety net for the case the cross-service mapping
            // can't help with — same anime listed on two providers with no
            // shared id (donghua, simulcast-only originals, recently licensed
            // shows). For each kept entry we record a normalised title token
            // set + year + format, then compare every new candidate against
            // the kept ones. Pairs collapse when:
            //   - Jaccard token overlap is >= 0.7 ("World Trigger Reboot" vs
            //     "World Trigger REBOOT Project (Provisional Title)" = 0.75), AND
            //   - years agree (or at least one is missing), AND
            //   - the coarse movie/not-movie format bucket agrees.
            // Conservative on purpose: a duplicate slipping through is far less
            // user-hostile than a real anime getting hidden.
            const double TITLE_SIMILARITY_THRESHOLD = 0.7;
            var titleSignatures = new List<(HashSet<string> Tokens, int? Year, string Format)>();
            var merged = new List<Meta>();
            var primaryService = primary.anime_service;

            for (var i = 0; i < results.Length; i++)
            {
                var batch = results[i];
                if (batch == null) continue;
                foreach (var m in batch)
                {
                    if (m == null || string.IsNullOrEmpty(m.id)) continue;

                    string dedupKey;
                    // With season-grouping on, the per-service fetch already
                    // collapsed this card onto its franchise group id (IMDb,
                    // then TMDB) via ResolveGroupedId — same precedence the
                    // Discover surfaces use. Keep that id so multi-cour
                    // franchises stay one card and the click-through lands on
                    // the franchise umbrella page; rewriting it to the
                    // primary's native id (what the ungrouped path does) would
                    // undo the grouping. Cards the mapping couldn't group fall
                    // through to the native id, so they take the primary-
                    // provider rewrite below — giving the IMDb → primary →
                    // original chain the request asked for.
                    if (groupSeasons && IsGroupedId(m.id))
                    {
                        dedupKey = m.id;
                    }
                    else
                    {
                        // Trakt has no anime id-space (GetIdWithPrefix would throw),
                        // so don't try to rewrite into it — an ungrouped entry under
                        // a Trakt primary keys on its canonical AniList id instead.
                        var primaryId = primaryService == AnimeService.Trakt
                            ? null
                            : await _mappingService.GetIdWithPrefixAsync(m.id, primaryService);
                        if (!string.IsNullOrEmpty(primaryId))
                        {
                            dedupKey = primaryId;
                            if (primaryId != m.id) m.id = primaryId;
                        }
                        else
                        {
                            var anilistId = primaryService == AnimeService.Anilist
                                ? null
                                : await _mappingService.GetIdWithPrefixAsync(m.id, AnimeService.Anilist);
                            dedupKey = anilistId ?? m.id;
                        }
                    }

                    if (!seen.Add(dedupKey)) continue;

                    var normalized = NormalizeTitle(m.name ?? string.Empty);
                    var tokens = string.IsNullOrEmpty(normalized)
                        ? new HashSet<string>()
                        : new HashSet<string>(normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    var fuzzyDup = false;
                    if (tokens.Count > 0)
                    {
                        foreach (var prev in titleSignatures)
                        {
                            if (prev.Tokens.Count == 0) continue;
                            if (m.year.HasValue && prev.Year.HasValue && m.year.Value != prev.Year.Value) continue;
                            if (FormatBucket(m.format) is { } fb && FormatBucket(prev.Format) is { } pb
                                && !string.Equals(fb, pb, StringComparison.Ordinal)) continue;
                            var intersectCount = tokens.Intersect(prev.Tokens).Count();
                            if (intersectCount == 0) continue;
                            var unionCount = tokens.Union(prev.Tokens).Count();
                            var jaccard = (double)intersectCount / unionCount;
                            if (jaccard >= TITLE_SIMILARITY_THRESHOLD)
                            {
                                fuzzyDup = true;
                                break;
                            }
                        }
                    }
                    if (fuzzyDup) continue;

                    merged.Add(m);
                    titleSignatures.Add((tokens, m.year, m.format));
                }
            }
            return merged;
        }

        /// <summary>
        /// Builds the anime slice of a Trakt user's list for the given status.
        /// Trakt has no anime concept — it tracks everything as movies / series
        /// (imdb-keyed) — so we pull both kinds for the status and keep only the
        /// entries whose imdb id resolves to a known anime in the cross-service
        /// mapping. The kept entries stay imdb-keyed (the franchise-group id
        /// space), so they dedup cleanly against the grouped AniList/MAL/Kitsu
        /// entries in the merge. Empty when Trakt isn't configured / connected.
        /// </summary>
        private async Task<List<Meta>> FetchTraktAnimeListAsync(string uid, ListType listType)
        {
            if (string.IsNullOrEmpty(uid) || !_traktService.IsConfigured) return [];

            var moviesTask = _traktService.GetListAsync(uid, listType, MetaType.movie);
            var seriesTask = _traktService.GetListAsync(uid, listType, MetaType.series);
            await Task.WhenAll(moviesTask, seriesTask);

            var metas = new List<Meta>();
            foreach (var item in moviesTask.Result.Concat(seriesTask.Result))
            {
                if (string.IsNullOrEmpty(item?.ImdbId)) continue;
                // Non-empty mapping ⇒ this imdb id is a known anime. The lookup
                // hits the in-memory mapping table (no network), so per-item is
                // cheap even across a long history.
                var mapping = await _mappingService.GetImdbMapping(item.ImdbId);
                if (mapping == null || mapping.Count == 0) continue;

                var meta = item.ToVideoMeta();
                // It's anime, not a general video — tag it so the card links to
                // the anime detail route (no ?type=) and the rest of the app
                // treats it as anime.
                meta.type = MetaType.anime.ToString();
                metas.Add(meta);
            }
            return metas;
        }

        /// <summary>
        /// Collapses a per-provider format label into a coarse bucket for the
        /// dedup's format guard. Only the movie/not-movie line matters there
        /// — a TV series and its theatrical recap are genuinely separate
        /// cards — so everything that isn't a movie (TV, TV Short, ONA, OVA,
        /// Special, ...) buckets to "series". Stops providers' fine-grained
        /// label drift (Kitsu "TV" vs AniList "TV Short" for the same show)
        /// from defeating the dedup. Returns null for an absent format so
        /// the guard treats "unknown" as "could match anything".
        /// </summary>
        private static string FormatBucket(string format)
        {
            if (string.IsNullOrWhiteSpace(format)) return null;
            return format.ToLowerInvariant().Contains("movie") ? "movie" : "series";
        }

        /// <summary>
        /// True when the id is a cross-service franchise group id (IMDb tt…
        /// or tmdb:…) rather than a service-native id. Used to tell the
        /// season-grouping path "this card is already grouped, leave its id
        /// alone" from the native-fallback case that still needs rewriting
        /// into the primary's id space.
        /// </summary>
        private static bool IsGroupedId(string id) =>
            !string.IsNullOrEmpty(id)
            && (id.StartsWith(imdbPrefix, StringComparison.Ordinal)
                || id.StartsWith(tmdbPrefix, StringComparison.Ordinal));
    }
}
