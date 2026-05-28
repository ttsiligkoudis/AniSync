using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace AnimeList.Services
{
    /// <summary>
    /// <see cref="AnilistFallback"/> partial: per-anime meta enrichment — recommendations,
    /// relations, supplementary links (tags / studios / staff bullets), external streaming
    /// links, trailer + per-episode video metadata, banner image. The "/anime/{id}" detail
    /// page consumes most of these; FetchSidedataAsync collapses the recommendations /
    /// relations / supplementary-links queries into one round-trip and caches the combined
    /// record.
    /// </summary>
    public partial class AnilistFallback
    {
        public async Task<List<Link>> GetRecommendationsAsync(int anilistId, AnimeService translateTo, bool groupSeasons)
        {
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            recommendations(sort: RATING_DESC, perPage: 25) {
                                edges {
                                    node {
                                        mediaRecommendation {
                                            id
                                            format
                                            title { english romaji }
                                        }
                                    }
                                }
                            }
                        }
                    }",
                variables = new { id = anilistId }
            });

            var data = await PostJsonAsync(requestBody);
            var edges = data?.Media?.recommendations?.edges;
            if (edges == null) return [];

            await _mappingService.EnsureLoadedAsync();

            var result = new List<Link>();
            foreach (var edge in edges)
            {
                var rec = edge.node?.mediaRecommendation;
                if (rec == null) continue;

                var recId = (int?)rec.id;
                if (!recId.HasValue) continue;

                var name = string.IsNullOrEmpty((string)rec.title?.english)
                    ? (string)rec.title?.romaji
                    : (string)rec.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                // Stremio-side "Similar" chip — points at web.stremio.com
                // so a chip tap opens the recommended anime inside Stremio
                // (or the native app via Universal / App Links) instead of
                // bouncing to anilist.co in a browser. The id is resolved
                // per-user (translateTo + groupSeasons): groupSeasons=true
                // hands back the franchise's imdb / tmdb id; otherwise
                // the user's primary's native kitsu: / mal: / anilist: id.
                // Falls back to anilist:N when the mapping table has no
                // matching cross-service entry.
                var resolvedId = await ResolveStremioIdAsync(recId.Value, translateTo, groupSeasons)
                    ?? $"{anilistPrefix}{recId.Value}";
                var recIsMovie = IsMovieFormat((string)rec.format);
                var recStremioType = recIsMovie ? MetaType.movie.ToString() : MetaType.series.ToString();
                var encodedId = Uri.EscapeDataString(resolvedId);
                result.Add(new Link
                {
                    name = name,
                    category = "Similar",
                    url = $"https://web.stremio.com/#/detail/{recStremioType}/{encodedId}",
                    anilistId = recId.Value,
                });
            }
            return result;
        }

        // The three public per-anime side-data fetchers (recommendations,
        // relations, supplementary links) all hit the same Media(id) root on
        // AniList. Fetching them separately meant up to three GraphQL
        // round-trips per detail render plus three independent cache entries.
        // FetchSidedataAsync collapses them into one combined query with one
        // cache entry; the three public methods below project off the cached
        // record so the call shape (and any caller logic) stays unchanged.
        private sealed record AnilistSidedata(
            List<Meta> Recommendations,
            List<Meta> Related,
            List<Link> SupplementaryLinks,
            List<Link> RelatedLinks,
            string BannerImage);

        public async Task<List<Meta>> GetRecommendationMetasAsync(int anilistId, AnimeService translateTo = AnimeService.Anilist, bool groupSeasons = false)
            => await TranslateMetaIdsAsync(
                CloneMetas((await FetchSidedataAsync(anilistId)).Recommendations),
                translateTo, groupSeasons);

        private async Task<AnilistSidedata> FetchSidedataAsync(int anilistId)
        {
            var cacheKey = $"anilist:sidedata:{anilistId}";
            if (_cache.TryGetValue<AnilistSidedata>(cacheKey, out var cached) && cached != null)
                return cached;

            var empty = new AnilistSidedata([], [], [], [], null);

            try
            {
                // One combined query: recommendations + relations + tags +
                // studios + staff + banner. All subselections share the
                // same Media(id) root so AniList resolves them in one go;
                // the complexity-budget cost is well within their 500
                // ceiling. bannerImage rides along so the watch / detail
                // hero can fall back to AniList's full-bleed banner
                // (~1900x800) when the user's primary service exposes a
                // lower-resolution image — MAL's pictures[0].large in
                // particular tops out at ~600-1000px and scales poorly
                // on big screens.
                var requestBody = SerializeObject(new
                {
                    query = @"
                        query ($id: Int) {
                            Media(id: $id, type: ANIME) {
                                bannerImage
                                recommendations(sort: RATING_DESC, perPage: 15) {
                                    edges {
                                        node {
                                            mediaRecommendation {
                                                id
                                                format
                                                episodes
                                                averageScore
                                                seasonYear
                                                title { english romaji }
                                                coverImage { large }
                                            }
                                        }
                                    }
                                }
                                relations {
                                    edges {
                                        relationType
                                        node {
                                            id
                                            type
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
                                tags { id name rank isAdult }
                                studios { edges { isMain node { id name siteUrl } } }
                                staff { edges { role node { id name { full } siteUrl } } }
                            }
                        }",
                    variables = new { id = anilistId }
                });

                var data = await PostJsonAsync(requestBody);
                var media = data?.Media;
                if (media == null) return empty;

                var sidedata = new AnilistSidedata(
                    Recommendations: ParseRecommendations(media),
                    Related: ParseRelated(media),
                    SupplementaryLinks: ParseSupplementaryLinks(media),
                    RelatedLinks: ParseRelatedLinks(media),
                    BannerImage: (string)media.bannerImage);

                // Only cache when at least one bucket is non-empty so a
                // transient AniList blip / rate-limit / 5xx doesn't lock in
                // an empty record for 24h.
                if (sidedata.Recommendations.Count > 0
                    || sidedata.Related.Count > 0
                    || sidedata.SupplementaryLinks.Count > 0
                    || sidedata.RelatedLinks.Count > 0
                    || !string.IsNullOrEmpty(sidedata.BannerImage))
                {
                    _cache.Set(cacheKey, sidedata, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = SeasonStatsCacheDuration,
                    });
                }
                return sidedata;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] FetchSidedataAsync failed: {ex.Message}");
                return empty;
            }
        }

        private static List<Meta> ParseRecommendations(dynamic media)
        {
            var edges = media?.recommendations?.edges;
            if (edges == null) return [];

            var result = new List<Meta>();
            foreach (var edge in edges)
            {
                var rec = edge.node?.mediaRecommendation;
                if (rec == null) continue;
                var recId = (int?)rec.id;
                if (!recId.HasValue) continue;
                var name = string.IsNullOrEmpty((string)rec.title?.english)
                    ? (string)rec.title?.romaji
                    : (string)rec.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                result.Add(new Meta
                {
                    id = $"{anilistPrefix}{recId.Value}",
                    name = name,
                    poster = (string)rec.coverImage?.large,
                    type = IsMovieFormat((string)rec.format)
                        ? MetaType.movie.ToString()
                        : MetaType.series.ToString(),
                    score = rec.averageScore != null
                        ? Math.Round((double)rec.averageScore / 10, 1)
                        : (double?)null,
                    episodes = (int?)rec.episodes,
                    year = (int?)rec.seasonYear,
                    format = NormalizeFormat((string)rec.format),
                });
            }
            return result;
        }

        /// <summary>
        /// Stremio-shape relation Links — Sequel / Prequel labels with
        /// web.stremio.com deep-link URLs. Mirrors the inline relation
        /// builder in <see cref="AnilistService"/> so the imdb-grouped
        /// enrichment in <see cref="MetaController"/> can inject the same
        /// chip strip the per-service paths emit natively. The Meta-
        /// returning <see cref="ParseRelated"/> collapses Sequel and
        /// Prequel into a single "Related" carousel for the web app's
        /// detail page — Stremio needs them tagged.
        /// </summary>
        private static List<Link> ParseRelatedLinks(dynamic media)
        {
            var edges = media?.relations?.edges;
            if (edges == null) return [];

            var relLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PREQUEL"] = "Prequel",
                ["SEQUEL"]  = "Sequel",
            };

            var result = new List<Link>();
            foreach (var edge in edges)
            {
                var relType = (string)edge.relationType;
                if (relType == null || !relLabels.TryGetValue(relType, out var label)) continue;

                var node = edge.node;
                if (node == null) continue;
                if ((bool?)node.isAdult == true) continue;
                if (!string.Equals((string)node.type, "ANIME", StringComparison.OrdinalIgnoreCase)) continue;

                var relId = (int?)node.id;
                if (!relId.HasValue) continue;

                var name = string.IsNullOrEmpty((string)node.title?.english)
                    ? (string)node.title?.romaji
                    : (string)node.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                var isRelMovie = IsMovieFormat((string)node.format);
                var stremioType = isRelMovie ? MetaType.movie.ToString() : MetaType.series.ToString();
                var encodedId = Uri.EscapeDataString($"{anilistPrefix}{relId.Value}");
                result.Add(new Link
                {
                    name = name,
                    category = label,
                    url = $"https://web.stremio.com/#/detail/{stremioType}/{encodedId}",
                    anilistId = relId.Value,
                });
            }
            return result;
        }

        private static List<Meta> ParseRelated(dynamic media)
        {
            var edges = media?.relations?.edges;
            if (edges == null) return [];

            var result = new List<Meta>();
            foreach (var edge in edges)
            {
                var relationType = (string)edge.relationType;
                if (relationType != "PREQUEL" && relationType != "SEQUEL") continue;

                var node = edge.node;
                if (node == null) continue;
                if ((string)node.type != "ANIME") continue;

                var relId = (int?)node.id;
                if (!relId.HasValue) continue;

                var name = string.IsNullOrEmpty((string)node.title?.english)
                    ? (string)node.title?.romaji
                    : (string)node.title?.english;
                if (string.IsNullOrEmpty(name)) continue;

                result.Add(new Meta
                {
                    id = $"{anilistPrefix}{relId.Value}",
                    name = name,
                    poster = (string)node.coverImage?.large,
                    type = IsMovieFormat((string)node.format)
                        ? MetaType.movie.ToString()
                        : MetaType.series.ToString(),
                    score = node.averageScore != null
                        ? Math.Round((double)node.averageScore / 10, 1)
                        : (double?)null,
                    episodes = (int?)node.episodes,
                    year = (int?)node.seasonYear,
                    format = NormalizeFormat((string)node.format),
                });
            }

            // Chronological-ish ordering so the carousel reads as a timeline.
            return result.OrderBy(m => m.year ?? int.MaxValue).ToList();
        }

        private static List<Link> ParseSupplementaryLinks(dynamic media)
        {
            var result = new List<Link>();

            if (media?.tags != null)
            {
                foreach (var tag in media.tags)
                {
                    if ((bool?)tag.isAdult == true) continue;
                    var rank = (int?)tag.rank ?? 0;
                    if (rank < 50) continue;
                    var name = (string)tag.name;
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add(new Link
                    {
                        name = name,
                        category = "Tag",
                        url = $"https://anilist.co/search/anime?genres={Uri.EscapeDataString(name)}",
                        anilistId = (long?)tag.id,
                    });
                }
            }

            if (media?.studios?.edges != null)
            {
                foreach (var edge in media.studios.edges)
                {
                    if ((bool?)edge.isMain != true) continue;
                    var name = (string)edge.node?.name;
                    var siteUrl = (string)edge.node?.siteUrl;
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add(new Link
                    {
                        name = name,
                        category = "Studio",
                        url = siteUrl,
                        anilistId = (long?)edge.node?.id,
                    });
                }
            }

            if (media?.staff?.edges != null)
            {
                foreach (var edge in media.staff.edges)
                {
                    var role = (string)edge.role;
                    var name = (string)edge.node?.name?.full;
                    var siteUrl = (string)edge.node?.siteUrl;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(role)) continue;
                    result.Add(new Link
                    {
                        name = name,
                        category = StaffRoleToCategory(role),
                        url = siteUrl,
                        anilistId = (long?)edge.node?.id,
                    });
                }
            }

            return result;
        }

        public async Task<List<StreamingLink>> GetExternalLinksAsync(int anilistId)
        {
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            externalLinks { site url type }
                        }
                    }",
                variables = new { id = anilistId }
            });

            var data = await PostJsonAsync(requestBody);
            var links = data?.Media?.externalLinks;
            if (links == null) return [];

            var result = new List<StreamingLink>();
            foreach (var link in links)
            {
                if ((string)link.type != "STREAMING") continue;
                var site = (string)link.site;
                var url = (string)link.url;
                if (string.IsNullOrEmpty(url)) continue;
                result.Add(new StreamingLink { Site = site, Url = url });
            }
            return result;
        }

        public async Task<string> GetYoutubeTrailerIdAsync(int anilistId)
        {
            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            trailer { id site }
                        }
                    }",
                variables = new { id = anilistId }
            });

            var data = await PostJsonAsync(requestBody);
            var trailer = data?.Media?.trailer;
            if (trailer == null) return null;
            // AniList stores YouTube videos as {site: "youtube", id: "<videoId>"}; other
            // hosters (Dailymotion etc.) are skipped because Stremio's Trailer model only
            // accepts a YouTube id.
            if ((string)trailer.site != "youtube") return null;
            return (string)trailer.id;
        }

        /// <summary>
        /// Picks the most stable id for the requested service, in priority order:
        /// IMDb &gt; TMDB &gt; service-native (kitsu/mal/anilist) &gt; anilist fallback.
        /// </summary>
        private async Task<string> ResolveExternalIdAsync(int anilistId, AnimeService translateTo)
        {
            var mapping = await _mappingService.GetAnilistMapping($"{anilistPrefix}{anilistId}");

            if (!string.IsNullOrEmpty(mapping?.ImdbId)) return mapping.ImdbId;
            if (!string.IsNullOrEmpty(mapping?.TmdbId)) return $"{tmdbPrefix}{mapping.TmdbId}";

            if (translateTo == AnimeService.Kitsu && mapping?.KitsuId != null)
                return $"{kitsuPrefix}{mapping.KitsuId}";
            if (translateTo == AnimeService.MyAnimeList && mapping?.MalId != null)
                return $"{malPrefix}{mapping.MalId}";

            return $"{anilistPrefix}{anilistId}";
        }

        /// <summary>
        /// Per-user Stremio meta id for an AniList anime id — picks the
        /// external/native form based on the caller's groupSeasons pref so
        /// a chip-built URL like web.stremio.com/#/detail/{type}/{id}
        /// resolves to whichever catalog the user's Stremio is configured
        /// for. groupSeasons=true prefers imdb / tmdb (franchise umbrella);
        /// false picks the primary's per-cour native id (kitsu:/mal:/
        /// anilist:). Falls back to anilist:N in either branch when the
        /// mapping row has no matching id.
        /// </summary>
        public async Task<string> ResolveStremioIdAsync(int anilistId, AnimeService translateTo, bool groupSeasons)
            => groupSeasons
                ? await ResolveExternalIdAsync(anilistId, translateTo)
                : await ResolveNativeIdAsync(anilistId, translateTo);

        public async Task<List<Video>> GetEpisodeVideosAsync(int anilistId)
        {
            if (anilistId <= 0) return [];

            var requestBody = SerializeObject(new
            {
                query = @"
                    query ($id: Int) {
                        Media(id: $id, type: ANIME) {
                            episodes
                            streamingEpisodes { title thumbnail }
                        }
                    }",
                variables = new { id = anilistId }
            });

            dynamic data;
            try { data = await PostJsonAsync(requestBody); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnilistFallback] GetEpisodeVideosAsync failed for {anilistId}: {ex.Message}");
                return [];
            }

            var media = data?.Media;
            if (media == null) return [];

            // Prefer streamingEpisodes — AniList carries real per-episode
            // titles + thumbnails for shows with a streaming-partner
            // listing (Crunchyroll / Funimation / etc.). When it's
            // empty, synthesise an "Episode N" list off the announced
            // episode count so the detail page still has rows to render.
            var result = new List<Video>();
            var streamingEps = media.streamingEpisodes as Newtonsoft.Json.Linq.JArray;
            if (streamingEps != null && streamingEps.Count > 0)
            {
                int i = 1;
                foreach (var ep in streamingEps)
                {
                    var title = (string)ep["title"];
                    var thumbnail = (string)ep["thumbnail"];
                    result.Add(new Video
                    {
                        title = string.IsNullOrEmpty(title) ? $"Episode {i}" : title,
                        thumbnail = thumbnail,
                        season = 1,
                        episode = i,
                    });
                    i++;
                }
                return result;
            }

            var totalEpisodes = (int?)media.episodes ?? 0;
            for (int i = 1; i <= totalEpisodes; i++)
            {
                result.Add(new Video
                {
                    title = $"Episode {i}",
                    season = 1,
                    episode = i,
                });
            }
            return result;
        }

        /// <summary>
        /// Browse-flavour id resolution — prefers the primary's NATIVE id
        /// (kitsu:N / mal:N / anilist:N) over the cross-service imdb/tmdb
        /// groupers. The browse routes (/discover/tag/{name},
        /// /discover/staff/{id}, /discover/studio/{id}) want each card to
        /// hand off to /anime/{id} with the
        /// user's primary's per-cour id so Manage Entry resolves directly
        /// instead of bouncing through an imdb-mapped franchise umbrella.
        /// Falls back to anilist:N when the requested service has no
        /// matching id on the mapping row.
        /// </summary>
        private async Task<string> ResolveNativeIdAsync(int anilistId, AnimeService translateTo)
        {
            // AniList primary (or anonymous, which defaults to Kitsu but
            // could just as well land back on AniList ids) — no lookup
            // needed, we already have the anilist id.
            if (translateTo == AnimeService.Anilist)
                return $"{anilistPrefix}{anilistId}";

            var mapping = await _mappingService.GetAnilistMapping($"{anilistPrefix}{anilistId}");

            if (translateTo == AnimeService.Kitsu && mapping?.KitsuId != null)
                return $"{kitsuPrefix}{mapping.KitsuId}";
            if (translateTo == AnimeService.MyAnimeList && mapping?.MalId != null)
                return $"{malPrefix}{mapping.MalId}";

            // No native mapping → ship the AniList id. The detail-page
            // controller already handles cross-service id resolution, so
            // /anime/anilist:N still renders for a Kitsu user; they just
            // won't be able to Manage Entry on it (which is the correct
            // outcome anyway, since the anime isn't on their service).
            return $"{anilistPrefix}{anilistId}";
        }

        private static string BuildAiringDescription(string raw, int? episode, long? airingAtUnix)
        {
            var prefix = string.Empty;
            if (episode.HasValue && airingAtUnix.HasValue)
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(airingAtUnix.Value).UtcDateTime;
                prefix = $"Episode {episode.Value} airs {when:yyyy-MM-dd HH:mm} UTC.\n\n";
            }
            return prefix + (raw ?? string.Empty);
        }

        public async Task<List<Meta>> GetRelatedAsync(int anilistId, AnimeService translateTo = AnimeService.Anilist, bool groupSeasons = false)
            => await TranslateMetaIdsAsync(
                CloneMetas((await FetchSidedataAsync(anilistId)).Related),
                translateTo, groupSeasons);

        public async Task<List<Link>> GetRelatedLinksAsync(int anilistId, AnimeService translateTo, bool groupSeasons)
        {
            var cached = (await FetchSidedataAsync(anilistId)).RelatedLinks;
            if (cached == null || cached.Count == 0) return [];

            // Sidedata is cached per AniList id only, so the URL the
            // ParseRelatedLinks builder stamps in carries the anilist:N
            // id. Translate per-user here so the cache stays sharable
            // across primaries while each caller still gets a Stremio
            // deep-link tagged with the id-space their addon catalogs.
            await _mappingService.EnsureLoadedAsync();
            var result = new List<Link>(cached.Count);
            foreach (var link in cached)
            {
                if (link == null) continue;
                result.Add(await RewriteLinkForPrimaryAsync(link, translateTo, groupSeasons));
            }
            return result;
        }

        /// <summary>
        /// Clone of a Sidedata-cached relation / recommendation Link with
        /// its web.stremio.com URL re-stamped for the caller's catalog id
        /// space. Detects movie vs series by peeking the cached URL's
        /// /detail/{type}/ segment — same shape ParseRelatedLinks /
        /// GetRecommendationsAsync builds, kept stable so we don't have to
        /// carry a parallel format buffer through the cached record.
        /// </summary>
        private async Task<Link> RewriteLinkForPrimaryAsync(Link source, AnimeService translateTo, bool groupSeasons)
        {
            var clone = new Link
            {
                name = source.name,
                category = source.category,
                url = source.url,
                anilistId = source.anilistId,
            };
            if (!source.anilistId.HasValue) return clone;

            var isMovie = source.url != null && source.url.Contains("/detail/movie/", StringComparison.Ordinal);
            var stremioType = isMovie ? MetaType.movie.ToString() : MetaType.series.ToString();
            var resolvedId = await ResolveStremioIdAsync((int)source.anilistId.Value, translateTo, groupSeasons)
                ?? $"{anilistPrefix}{source.anilistId.Value}";
            clone.url = $"https://web.stremio.com/#/detail/{stremioType}/{Uri.EscapeDataString(resolvedId)}";
            return clone;
        }

        public async Task<List<Link>> GetSupplementaryLinksAsync(int anilistId)
            => (await FetchSidedataAsync(anilistId)).SupplementaryLinks;

        public async Task<string> GetBannerImageAsync(int anilistId)
            => (await FetchSidedataAsync(anilistId)).BannerImage;
    }
}
