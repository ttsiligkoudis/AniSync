using AnimeList.Services.Interfaces;
using HtmlAgilityPack;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace AnimeList.Services
{
    /// <summary>
    /// Scrapes AnimeFillerList show pages for episode-category data and caches the
    /// result. Negative lookups (slug doesn't exist on AFL) are cached short so we
    /// don't pound their server re-asking; positive lookups are cached long because
    /// filler designations change infrequently after a show finishes airing.
    /// </summary>
    public class FillerListService : IFillerListService
    {
        private const string BaseUrl = "https://www.animefillerlist.com/shows/";

        private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(30);
        private static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(1);

        // Static so the singleton service's cache persists across the process lifetime.
        // Keyed on the resolved slug rather than the raw anime title so two services
        // returning slightly different titles ("Naruto" / "naruto") share the entry.
        private static readonly ConcurrentDictionary<string, (DateTime Expires, Dictionary<int, string> Data)> _cache = new();

        private static readonly Regex SlugSafe = new("[^a-z0-9]+", RegexOptions.Compiled);

        private readonly IHttpClientFactory _clientFactory;

        public FillerListService(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory;
        }

        public async Task<Dictionary<int, string>> GetEpisodeCategoriesAsync(string animeName)
        {
            if (string.IsNullOrWhiteSpace(animeName)) return [];

            var slug = Slugify(animeName);
            if (string.IsNullOrEmpty(slug)) return [];

            if (_cache.TryGetValue(slug, out var cached) && DateTime.UtcNow < cached.Expires)
                return cached.Data;

            var data = await FetchAndParseAsync(slug);

            // Cache the result with a TTL that reflects whether AFL knew the show.
            // Empty dict => 24h (in case the show airs and gets added later); non-empty
            // => 30d since filler-vs-canon classifications rarely change once an arc's
            // designation has settled.
            var ttl = data.Count == 0 ? NegativeTtl : PositiveTtl;
            _cache[slug] = (DateTime.UtcNow + ttl, data);
            return data;
        }

        private async Task<Dictionary<int, string>> FetchAndParseAsync(string slug)
        {
            var url = BaseUrl + slug;
            try
            {
                var client = _clientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                // Polite User-Agent so AFL can identify the traffic and contact us
                // (rather than IP-banning silently) if our scraping ever bothers them.
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AniSync", "1.0"));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://anisync.fly.dev)"));

                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode) return [];

                var html = await response.Content.ReadAsStringAsync();
                return Parse(html);
            }
            catch
            {
                // Best-effort. Network blip / parse error / AFL down — return empty,
                // negative-cache it, move on. The meta page just won't show filler
                // markers for this show until the negative cache expires.
                return [];
            }
        }

        /// <summary>
        /// AFL's show pages render episode rows inside tables on the page. Each row's
        /// <c>data-category</c> attribute holds one of "Manga Canon", "Anime Canon",
        /// "Mixed Canon/Filler", "Filler" — we normalise those to canon / filler /
        /// mixed. Episode number is the first column. Falls back to scanning visible
        /// columns when data attributes change shape, so a layout tweak on AFL doesn't
        /// silently kill the integration.
        /// </summary>
        internal static Dictionary<int, string> Parse(string html)
        {
            var result = new Dictionary<int, string>();
            if (string.IsNullOrWhiteSpace(html)) return result;

            var doc = new HtmlDocument { OptionFixNestedTags = true };
            doc.LoadHtml(html);

            var rows = doc.DocumentNode.SelectNodes("//table//tr");
            if (rows == null) return result;

            foreach (var row in rows)
            {
                // First cell text is usually the episode number. Some layouts include
                // a header cell (<th>) which we skip implicitly because int.TryParse
                // fails on the header text.
                var cells = row.SelectNodes(".//td");
                if (cells == null || cells.Count == 0) continue;

                var firstCell = cells[0].InnerText?.Trim();
                if (!int.TryParse(firstCell, out var episode)) continue;

                // Try the data-attribute first (less fragile); fall back to the last
                // visible cell which AFL typically uses for the type column.
                var category = row.GetAttributeValue("data-category", null)
                               ?? cells[^1].InnerText?.Trim();
                var normalised = NormaliseCategory(category);
                if (normalised == null) continue;

                // If the same episode shows up twice (multi-table layouts), the first
                // occurrence wins — typically the more authoritative one.
                result.TryAdd(episode, normalised);
            }

            return result;
        }

        private static string NormaliseCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return null;
            var c = category.ToLowerInvariant();
            if (c.Contains("filler") && c.Contains("canon")) return "mixed";
            if (c.Contains("filler")) return "filler";
            if (c.Contains("canon")) return "canon";
            return null;
        }

        /// <summary>
        /// Lowercases, drops anything that isn't a-z or 0-9, collapses runs of those
        /// drops to a single hyphen. Mirrors AFL's own slug convention closely enough
        /// for popular shows ("One Piece" → "one-piece", "Naruto: Shippuden" →
        /// "naruto-shippuden") without an explicit ID-to-slug map.
        /// </summary>
        internal static string Slugify(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var lower = name.ToLowerInvariant();
            var slug = SlugSafe.Replace(lower, "-").Trim('-');
            return slug;
        }
    }
}
