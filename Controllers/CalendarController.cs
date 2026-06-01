using System.Globalization;
using AnimeList.Models;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Month-grid Calendar page. Shows recent + upcoming episodes for the shows a
    /// user tracks: anime from their Watching list (AniList airing schedule,
    /// matched through the watching cache) and series from their Trakt Watching +
    /// Planning lists (the same scope the series notifications use). Each cell links
    /// straight to that episode's watch page. Server-rendered; month navigation is
    /// plain ?y=&amp;m= links so there's no client-side state to keep in sync.
    /// <para>
    /// Episodes are bucketed by their UTC calendar day — a known simplification: an
    /// episode airing late in the day in a far-west timezone can land on the next
    /// grid cell relative to the viewer's local day.
    /// </para>
    /// </summary>
    public class CalendarController : Controller
    {
        private readonly ITokenService _tokenService;
        private readonly IConfigStore _configStore;
        private readonly IWatchingCacheStore _watchingCache;
        private readonly IAnimeMappingService _mapping;
        private readonly IAnilistFallback _anilist;
        private readonly ITraktService _trakt;
        private readonly ILogger<CalendarController> _logger;

        public CalendarController(
            ITokenService tokenService,
            IConfigStore configStore,
            IWatchingCacheStore watchingCache,
            IAnimeMappingService mapping,
            IAnilistFallback anilist,
            ITraktService trakt,
            ILogger<CalendarController> logger)
        {
            _tokenService = tokenService;
            _configStore = configStore;
            _watchingCache = watchingCache;
            _mapping = mapping;
            _anilist = anilist;
            _trakt = trakt;
            _logger = logger;
        }

        [HttpGet("/calendar")]
        public async Task<IActionResult> Index(int? y, int? m)
        {
            var (_, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            if (uid == null) return RedirectToAction("Index", "Home");

            var nowUtc = DateTime.UtcNow;
            var year = y ?? nowUtc.Year;
            var month = m ?? nowUtc.Month;
            if (month is < 1 or > 12) month = nowUtc.Month;
            if (year is < 1970 or > 9999) year = nowUtc.Year;

            // Sunday-first 6×7-or-5×7 grid covering the whole month plus the
            // leading/trailing days that fill its first and last weeks.
            var firstOfMonth = new DateOnly(year, month, 1);
            var leading = (int)firstOfMonth.DayOfWeek; // Sunday == 0
            var gridStart = firstOfMonth.AddDays(-leading);
            var daysInMonth = DateTime.DaysInMonth(year, month);
            var cellCount = (int)Math.Ceiling((leading + daysInMonth) / 7.0) * 7;

            var rangeStart = new DateTimeOffset(gridStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var rangeEnd = rangeStart.AddDays(cellCount);

            var byDate = new Dictionary<DateOnly, List<CalendarEpisode>>();
            void Bucket(CalendarEpisode ep)
            {
                var d = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(ep.AiringAt).UtcDateTime);
                if (!byDate.TryGetValue(d, out var list))
                {
                    list = new List<CalendarEpisode>();
                    byDate[d] = list;
                }
                list.Add(ep);
            }

            // One source failing (AniList down, Trakt unreachable) must not blank
            // the whole calendar — degrade to the other source.
            try { await AddAnimeAsync(uid, rangeStart.ToUnixTimeSeconds(), rangeEnd.ToUnixTimeSeconds() - 1, Bucket); }
            catch (Exception ex) { _logger.LogWarning(ex, "calendar anime load failed for {Uid}", uid); }

            try { await AddSeriesAsync(uid, gridStart, cellCount, rangeStart, rangeEnd, Bucket); }
            catch (Exception ex) { _logger.LogWarning(ex, "calendar series load failed for {Uid}", uid); }

            var today = DateOnly.FromDateTime(nowUtc);
            var days = new List<CalendarDay>(cellCount);
            var total = 0;
            for (var i = 0; i < cellCount; i++)
            {
                var date = gridStart.AddDays(i);
                byDate.TryGetValue(date, out var eps);
                eps ??= new List<CalendarEpisode>();
                eps.Sort((a, b) => a.AiringAt.CompareTo(b.AiringAt));
                total += eps.Count;
                days.Add(new CalendarDay
                {
                    Date = date,
                    InMonth = date.Year == year && date.Month == month,
                    IsToday = date == today,
                    Episodes = eps,
                });
            }

            var prev = firstOfMonth.AddMonths(-1);
            var next = firstOfMonth.AddMonths(1);
            ViewData["ActiveNav"] = "calendar";
            ViewData["Title"] = "Calendar";
            return View(new CalendarViewModel
            {
                Year = year,
                Month = month,
                MonthLabel = firstOfMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                PrevYear = prev.Year,
                PrevMonth = prev.Month,
                NextYear = next.Year,
                NextMonth = next.Month,
                IsCurrentMonth = year == nowUtc.Year && month == nowUtc.Month,
                Days = days,
                TotalEpisodes = total,
            });
        }

        // Anime: the user's Watching cache (prefixed ids in their primary service
        // space) → AniList ids → batched airing schedule. The user-space id is kept
        // for the deep-link so it lands on the id-space the rest of the site uses.
        private async Task AddAnimeAsync(string uid, long startUnix, long endUnix, Action<CalendarEpisode> bucket)
        {
            var cache = await _watchingCache.GetAsync(uid);
            if (cache == null || cache.MediaIds.Count == 0) return;

            var linkByAnilist = new Dictionary<int, string>();
            foreach (var cacheId in cache.MediaIds)
            {
                int? anilistId = null;
                if (cache.Service == AnimeService.Anilist)
                {
                    if (cacheId.StartsWith(anilistPrefix, StringComparison.Ordinal)
                        && int.TryParse(cacheId.AsSpan(anilistPrefix.Length), out var parsed))
                        anilistId = parsed;
                }
                else
                {
                    // Map MAL/Kitsu ids back to AniList so we can query the schedule.
                    var prefixed = await _mapping.GetIdWithPrefixAsync(cacheId, AnimeService.Anilist);
                    if (!string.IsNullOrEmpty(prefixed)
                        && prefixed.StartsWith(anilistPrefix, StringComparison.Ordinal)
                        && int.TryParse(prefixed.AsSpan(anilistPrefix.Length), out var parsed))
                        anilistId = parsed;
                }
                if (anilistId.HasValue)
                    linkByAnilist.TryAdd(anilistId.Value, cacheId);
            }
            if (linkByAnilist.Count == 0) return;

            var airings = await _anilist.GetAiringForMediaAsync(linkByAnilist.Keys.ToList(), startUnix, endUnix);
            foreach (var a in airings)
            {
                if (!linkByAnilist.TryGetValue(a.AnilistId, out var userId)) continue;
                bucket(new CalendarEpisode(
                    Kind: "anime",
                    Title: a.Title,
                    Season: null,
                    Episode: a.Episode,
                    AiringAt: a.AiringAt,
                    CoverImage: a.CoverImage,
                    LinkPath: $"/meta/{userId}/watch/{a.Episode}"));
            }
        }

        // Series: Trakt "my shows" calendar, filtered to the user's Watching
        // (playback) + Planning (watchlist) shows — the same scope the series
        // episode notifications use, so the calendar and the bell agree.
        private async Task AddSeriesAsync(
            string uid, DateOnly gridStart, int days,
            DateTimeOffset rangeStart, DateTimeOffset rangeEnd, Action<CalendarEpisode> bucket)
        {
            if (!_trakt.IsConfigured) return;
            if (await _trakt.GetValidTokenAsync(uid) == null) return;

            var planning = await _trakt.GetWatchlistAsync(uid);
            var watching = await _trakt.GetPlaybackAsync(uid);
            var eligible = planning.Concat(watching)
                .Where(i => i.Type == "series" && !string.IsNullOrEmpty(i.ImdbId))
                .Select(i => i.ImdbId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (eligible.Count == 0) return;

            var calendar = await _trakt.GetMyShowsCalendarAsync(uid, gridStart, days);
            foreach (var e in calendar)
            {
                if (string.IsNullOrEmpty(e.ImdbId) || !eligible.Contains(e.ImdbId)) continue;
                if (e.FirstAired is not { } aired) continue;
                if (aired < rangeStart || aired >= rangeEnd) continue;

                bucket(new CalendarEpisode(
                    Kind: "series",
                    Title: e.ShowTitle,
                    Season: e.Season,
                    Episode: e.Episode,
                    AiringAt: aired.ToUnixTimeSeconds(),
                    CoverImage: e.ThumbnailUrl,
                    LinkPath: $"/meta/{e.ImdbId}/watch/{e.Season}/{e.Episode}?type=series"));
            }
        }
    }
}
