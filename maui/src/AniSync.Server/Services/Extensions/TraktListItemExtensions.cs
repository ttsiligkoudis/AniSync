using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeList.Models;
using AnimeList.Services.Interfaces;

namespace AnimeList.Services.Extensions
{
    public static class TraktListItemExtensions
    {
        // Drops any video poster card whose imdb id (Meta.id, set by ToVideoMeta
        // and by Cinemeta) resolves to a known anime in the cross-service
        // mapping. Anime is tracked on the AniList side, so it must not surface
        // on the Trakt / Cinemeta video surfaces (library, browse, search). This
        // is the inverse of the keep-only-anime filter MergedListService applies
        // to fold Trakt anime into the AniList list. Items with no id / no
        // mapping are kept (not anime). The lookup hits the in-memory mapping
        // table after first load, so per-item is cheap even across a long list.
        public static async Task<List<Meta>> ExcludeAnimeAsync(
            this List<Meta> metas, IAnimeMappingService mapping)
        {
            if (metas == null || metas.Count == 0) return metas ?? new List<Meta>();
            var kept = new List<Meta>(metas.Count);
            foreach (var m in metas)
            {
                if (await mapping.IsAnimeImdbAsync(m?.id)) continue;
                kept.Add(m);
            }
            return kept;
        }

        // Maps a list of Trakt items to poster cards, dropping any without an
        // imdb id (the video routes / metahub fallback both key off it) and
        // preserving order.
        public static List<Meta> ToVideoMetas(this IEnumerable<TraktListItem> items)
            => (items ?? Enumerable.Empty<TraktListItem>())
                .Where(it => !string.IsNullOrEmpty(it.ImdbId))
                .Select(it => it.ToVideoMeta())
                .ToList();

        // Builds a poster-card Meta from a Trakt discovery / related item.
        // Full-Trakt: Trakt's own artwork first, metahub (Cinemeta's image CDN,
        // keyed by IMDb id) as the fallback when Trakt has no poster — so a card
        // still renders an image without a per-item Cinemeta meta round-trip.
        public static Meta ToVideoMeta(this TraktListItem it)
        {
            return new Meta
            {
                id = it.ImdbId,
                name = it.Title,
                year = it.Year,
                type = it.Type == "movie" ? MetaType.movie.ToString() : MetaType.series.ToString(),
                poster = !string.IsNullOrEmpty(it.Poster)
                    ? it.Poster
                    : $"https://images.metahub.space/poster/medium/{it.ImdbId}/img",
                background = it.Background,
                score = it.Rating is double r && r > 0 ? System.Math.Round(r, 1) : null,
            };
        }
    }
}
