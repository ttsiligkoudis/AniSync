using System.Globalization;
using AnimeList.Models;
using AnimeList.Services.Extensions;
using AnimeList.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AnimeList.Controllers
{
    /// <summary>
    /// Weekly Calendar page. Shows recent + upcoming episodes for the shows a user
    /// tracks, one week at a time, as a day-by-day agenda so every entry can carry
    /// its title (a month grid only has room for dots on mobile). Anime come from
    /// the user's Watching list (AniList airing schedule, matched through the
    /// watching cache); series from their Trakt Watching + Planning lists (the same
    /// scope the series notifications use). Each row links straight to that
    /// episode's watch page. Server-rendered; week navigation is plain ?d= links so
    /// there's no client-side state to keep in sync.
    /// <para>
    /// Episodes are bucketed by their UTC calendar day — a known simplification: an
    /// episode airing late in the day in a far-west timezone can land on the next
    /// day relative to the viewer's local day. The displayed air time is localised
    /// client-side from the Unix timestamp.
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
        public async Task<IActionResult> Index(string d)
        {
            var (_, uid) = await _tokenService.ResolveCurrentAsync(_configStore);
            if (uid == null) return RedirectToAction("Index", "Home");

            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            // ?d=yyyy-MM-dd is the selected day; default = today.
            var selectedDate = DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)
                ? parsed
                : today;

            // Sunday-first week containing the selected day.
            var weekStart = selectedDate.AddDays(-(int)selectedDate.DayOfWeek);
            const int days = 7;

            var rangeStart = new DateTimeOffset(weekStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var rangeEnd = rangeStart.AddDays(days);

            var byDate = new Dictionary<DateOnly, List<CalendarEpisode>>();
            void Bucket(CalendarEpisode ep)
            {
                var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(ep.AiringAt).UtcDateTime);
                if (!byDate.TryGetValue(date, out var list))
                {
                    list = new List<CalendarEpisode>();
                    byDate[date] = list;
                }
                list.Add(ep);
            }

            // Honour the "Group anime seasons" preference for the anime deep-links,
            // same as the notifications bell + catalog renders.
            var cfg = await GetConfigByUidAsync(uid, _configStore);
            var groupSeasons = cfg?.enableSeasonGrouping == true;

            // One source failing (AniList down, Trakt unreachable) must not blank
            // the whole calendar — degrade to the other source.
            try { await AddAnimeAsync(uid, rangeStart.ToUnixTimeSeconds(), rangeEnd.ToUnixTimeSeconds() - 1, groupSeasons, Bucket); }
            catch (Exception ex) { _logger.LogWarning(ex, "calendar anime load failed for {Uid}", uid); }

            try { await AddSeriesAsync(uid, weekStart, days, rangeStart, rangeEnd, Bucket); }
            catch (Exception ex) { _logger.LogWarning(ex, "calendar series load failed for {Uid}", uid); }

            var dayList = new List<CalendarDay>(days);
            var total = 0;
            for (var i = 0; i < days; i++)
            {
                var date = weekStart.AddDays(i);
                byDate.TryGetValue(date, out var eps);
                eps ??= new List<CalendarEpisode>();
                eps.Sort((a, b) => a.AiringAt.CompareTo(b.AiringAt));
                total += eps.Count;

                dayList.Add(new CalendarDay
                {
                    Date = date,
                    DateIso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Label = BuildSectionLabel(date),
                    IsToday = date == today,
                    IsSelected = date == selectedDate,
                    HasAnime = eps.Any(e => e.Kind == "anime"),
                    HasSeries = eps.Any(e => e.Kind == "series"),
                    Episodes = eps,
                });
            }

            ViewData["ActiveNav"] = "calendar";
            ViewData["Title"] = "Calendar";
            return View(new CalendarViewModel
            {
                Days = dayList,
                SelectedDateIso = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ScrollToSelected = !string.IsNullOrEmpty(d),
                PrevDate = selectedDate.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                NextDate = selectedDate.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                SelectedIsToday = selectedDate == today,
                TotalEpisodes = total,
            });
        }

        // "Friday, May 29th" — weekday, month, ordinal day (per-day section heading).
        private static string BuildSectionLabel(DateOnly date)
        {
            var ci = CultureInfo.InvariantCulture;
            var d = date.Day;
            var suffix = (d % 100) is >= 11 and <= 13
                ? "th"
                : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            return $"{date.ToString("dddd", ci)}, {date.ToString("MMMM", ci)} {d}{suffix}";
        }

        // Anime: the user's Watching cache (prefixed ids in their primary service
        // space) → AniList ids → batched airing schedule. The deep-link respects the
        // "Group anime seasons" preference: when on, it points at the IMDb franchise
        // umbrella with the cour's season embedded (same shape the notification
        // rewriter / catalog use); when off, the user-space per-service id is kept.
        private async Task AddAnimeAsync(string uid, long startUnix, long endUnix, bool groupSeasons, Action<CalendarEpisode> bucket)
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

            // When grouping, resolve each AniList id to its IMDb umbrella + cour season
            // once and reuse across that show's episodes. Null = no usable imdb mapping
            // (fall back to the ungrouped per-service link).
            var grouped = new Dictionary<int, (string Imdb, int? Season)?>();

            var airings = await _anilist.GetAiringForMediaAsync(linkByAnilist.Keys.ToList(), startUnix, endUnix);
            foreach (var a in airings)
            {
                if (!linkByAnilist.TryGetValue(a.AnilistId, out var userId)) continue;

                string link = null;
                if (groupSeasons)
                {
                    if (!grouped.TryGetValue(a.AnilistId, out var g))
                    {
                        g = null;
                        try
                        {
                            var m = await _mapping.GetAnilistMapping($"{anilistPrefix}{a.AnilistId}");
                            if (m != null && !string.IsNullOrEmpty(m.ImdbId)
                                && m.ImdbId.StartsWith("tt", StringComparison.Ordinal))
                                g = (m.ImdbId, m.Season);
                        }
                        catch { g = null; }
                        grouped[a.AnilistId] = g;
                    }
                    if (g is { } gv)
                        link = gv.Season is int s && s > 0
                            ? $"/meta/{gv.Imdb}/watch/{s}/{a.Episode}"
                            : $"/meta/{gv.Imdb}/watch/{a.Episode}";
                }
                link ??= $"/meta/{userId}/watch/{a.Episode}";

                bucket(new CalendarEpisode(
                    Kind: "anime",
                    Title: a.Title,
                    Season: null,
                    Episode: a.Episode,
                    AiringAt: a.AiringAt,
                    CoverImage: a.CoverImage,
                    LinkPath: link));
            }
        }

        // Series: Trakt "my shows" calendar, filtered to the user's Watching
        // (playback) + Planning (watchlist) shows — the same scope the series
        // episode notifications use, so the calendar and the bell agree.
        private async Task AddSeriesAsync(
            string uid, DateOnly weekStart, int days,
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

            var calendar = await _trakt.GetMyShowsCalendarAsync(uid, weekStart, days);
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
                    LinkPath: $"/meta/{e.ImdbId}/watch/{e.Season}/{e.Episode}?type=series",
                    EpisodeTitle: e.EpisodeTitle));
            }
        }
    }
}
