using System.Collections.Generic;
using System.Linq;
using AnimeList.Models;

namespace AnimeList.Services.Extensions
{
    public static class TraktListItemExtensions
    {
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
