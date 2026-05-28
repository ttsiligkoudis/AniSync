using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace AnimeList.Services
{
    /// <summary>
    /// Stateless anonymous AniList client used by cross-service fallbacks.
    /// </summary>
    public partial class AnilistFallback : IAnilistFallback
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IAnimeMappingService _mappingService;
        private readonly IMemoryCache _cache;
        private const string _api = "https://graphql.anilist.co";
        private const int PageSize = 50;
        // 24-hour TTL on the seasonal stat counts — the catalog moves slowly
        // (a few additions per week, status flips on Tuesdays, etc.) so day-
        // stale numbers are fine. Keyed by (season, year) so the cache
        // naturally rotates when AniList moves to the next season; the old
        // season's entry just expires unread.
        private static readonly TimeSpan SeasonStatsCacheDuration = TimeSpan.FromHours(24);


        public AnilistFallback(IHttpClientFactory clientFactory, IAnimeMappingService mappingService, IMemoryCache cache)
        {
            _clientFactory = clientFactory;
            _mappingService = mappingService;
            _cache = cache;
        }

        private async Task<dynamic> PostJsonAsync(string requestBody)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _api)
            {
                Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json")
            };

            var client = _clientFactory.CreateClient();
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                AnilistHealthMonitor.RecordFailure();
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500) AnilistHealthMonitor.RecordFailure();
                return null;
            }
            AnilistHealthMonitor.RecordSuccess();

            var content = await response.Content.ReadAsStringAsync();
            return DeserializeObject<dynamic>(content)?.data;
        }

        // Sidedata is cached in-memory; we Clone the metas before
        // translation so the per-call native-id translation doesn't mutate
        // the cached list (which would leak across viewers with different
        // primary services).
        private static List<Meta> CloneMetas(List<Meta> source)
        {
            if (source == null || source.Count == 0) return [];
            var copy = new List<Meta>(source.Count);
            foreach (var m in source)
            {
                copy.Add(new Meta
                {
                    id = m.id,
                    name = m.name,
                    poster = m.poster,
                    type = m.type,
                    score = m.score,
                    episodes = m.episodes,
                    year = m.year,
                    format = m.format,
                    airingEpisode = m.airingEpisode,
                    airingAt = m.airingAt,
                });
            }
            return copy;
        }

        public async Task<List<Meta>> TranslateMetaIdsAsync(List<Meta> metas, AnimeService translateTo, bool groupSeasons = false)
        {
            if (metas == null || metas.Count == 0) return metas;
            // groupSeasons=true short-circuits the AniList early-return below:
            // an AniList primary with grouping on still wants IMDb-prefixed ids
            // on the cards so click-throughs land on the franchise umbrella
            // page (per /configure's "Group anime seasons" toggle), not on the
            // per-cour anilist:N detail.
            if (translateTo == AnimeService.Anilist && !groupSeasons) return metas;
            await _mappingService.EnsureLoadedAsync();
            foreach (var meta in metas)
            {
                if (string.IsNullOrEmpty(meta?.id) || !meta.id.StartsWith(anilistPrefix)) continue;
                if (!int.TryParse(meta.id[anilistPrefix.Length..], out var anilistId)) continue;
                meta.id = groupSeasons
                    ? await ResolveExternalIdAsync(anilistId, translateTo)
                    : await ResolveNativeIdAsync(anilistId, translateTo);
            }
            return metas;
        }

        /// <summary>
        /// Per-user id rewrite for metas that already carry a service-native
        /// or imdb id (i.e. weren't produced by AnilistFallback and so didn't
        /// pass through <see cref="TranslateMetaIdsAsync"/>). Handles
        /// recommendations + the search dedup post-pass: per-service
        /// GetAnimeByIdAsync emits mal:/kitsu:/anilist: ids depending on the
        /// caller's primary, but a user with grouping on wants every card to
        /// surface as imdb:tt... so franchise click-throughs land on the
        /// canonical /anime/imdb:tt... page everywhere. groupSeasons=false is
        /// the no-op pass-through; ids the mapping table doesn't recognise
        /// stay unchanged either way so the card still links somewhere
        /// reasonable.
        /// </summary>
        public async Task<List<Meta>> ApplyGroupingToMetasAsync(List<Meta> metas, bool groupSeasons)
        {
            if (!groupSeasons || metas == null || metas.Count == 0) return metas;
            await _mappingService.EnsureLoadedAsync();
            foreach (var meta in metas)
            {
                if (string.IsNullOrEmpty(meta?.id)) continue;
                // Already imdb-prefixed → nothing to rewrite. tmdb is also
                // an acceptable grouped id (next-best after imdb in the
                // ResolveGroupedId chain) so we leave it alone too.
                if (meta.id.StartsWith(imdbPrefix) || meta.id.StartsWith(tmdbPrefix)) continue;

                var imdbId = await ResolveImdbAsync(meta.id);
                if (!string.IsNullOrEmpty(imdbId)) meta.id = imdbId;
            }
            return metas;
        }

        /// <summary>
        /// Resolves any service-prefixed id (anilist:/mal:/kitsu:) to the
        /// imdb tt-id from the cross-service mapping table, or null when no
        /// imdb mapping exists. Shared between the meta-translation path
        /// above and the notification-link rewrite the dispatcher uses to
        /// honor the per-user grouping pref when constructing the click
        /// target.
        /// </summary>
        private async Task<string> ResolveImdbAsync(string animeId)
        {
            if (string.IsNullOrEmpty(animeId)) return null;
            AnimeIdMapping mapping = null;
            try
            {
                if (animeId.StartsWith(anilistPrefix))
                    mapping = await _mappingService.GetAnilistMapping(animeId);
                else if (animeId.StartsWith(malPrefix))
                    mapping = await _mappingService.GetMalMapping(animeId);
                else if (animeId.StartsWith(kitsuPrefix))
                    mapping = await _mappingService.GetKitsuMapping(animeId);
            }
            catch { return null; }
            var imdb = mapping?.ImdbId;
            return !string.IsNullOrEmpty(imdb) && imdb.StartsWith("tt") ? imdb : null;
        }

        // Mirrors AnilistService.StaffRoleToCategory — keeps the augmented
        // page's link categories identical to a native AniList load.
        private static string StaffRoleToCategory(string role)
        {
            var r = role.ToLowerInvariant();
            if (r.Contains("director")) return "director";
            if (r.Contains("writ") || r.Contains("script") || r.Contains("creator")) return "writer";
            if (r.Contains("composer") || r.Contains("music")) return "Composer";
            if (r.Contains("character design") || r.Contains("art")) return "Artist";
            if (r.Contains("producer")) return "Producer";
            return "Staff";
        }

        // Shared poster builder for tag / staff / studio browse queries —
        // same shape: each entry has id + format + title + coverImage +
        // averageScore + seasonYear + episodes. Translates ids into the
        // requested service's NATIVE id (kitsu:N / mal:N / anilist:N), not
        // the imdb/tmdb cross-service groupers — clicks should land on the
        // user's primary's per-cour detail page so Manage Entry resolves
        // directly. AniList id is the fallback when no native mapping
        // exists for the requested service.
        private async Task<List<Meta>> BuildBrowseMetasAsync(dynamic mediaArr, AnimeService translateTo, bool hideAdult = false, bool groupSeasons = false)
        {
            if (mediaArr == null) return [];
            await _mappingService.EnsureLoadedAsync();
            var seen = new HashSet<string>();
            var result = new List<Meta>();
            foreach (var media in mediaArr)
            {
                var anilistId = (int?)media.id;
                if (!anilistId.HasValue) continue;

                // 18+ gate — per-user setting threaded through from the
                // discover-side controllers. Each query selects isAdult so
                // we can filter here without a second round-trip; queries
                // that need a free-text-tag style filter (Page.media) also
                // pass isAdult:false at the GraphQL layer, but the
                // Staff.staffMedia / Studio.media connections don't expose
                // that arg so the client-side filter is what enforces it
                // for those paths.
                if (hideAdult && (bool?)media.isAdult == true) continue;

                // Per-user grouping pref picks the id-resolver: imdb-first
                // when on (matches Stremio's grouped catalog id space), the
                // primary's native id when off (so Manage Entry / detail
                // hand-offs land on the per-cour page). seen-set dedup
                // collapses cours that share an imdb id into a single card
                // when grouping is on.
                var externalId = groupSeasons
                    ? await ResolveExternalIdAsync(anilistId.Value, translateTo)
                    : await ResolveNativeIdAsync(anilistId.Value, translateTo);
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
                    score = media.averageScore != null
                        ? Math.Round((double)media.averageScore / 10, 1)
                        : (double?)null,
                    episodes = (int?)media.episodes,
                    year = (int?)media.seasonYear,
                    format = NormalizeFormat((string)media.format),
                });
            }
            // Sort by the display name we just built (english title, falling
            // back to romaji) so the result order matches what the user
            // actually reads on the cards. Library does the same; consistency
            // between browse surfaces lets the user predict where a specific
            // anime will be in the grid.
            return result
                .OrderBy(m => m.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

    }
}
