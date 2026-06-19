using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace AnimeList.Services
{
    /// <summary>
    /// introdb.app client with a per-(IMDb id, season, episode) in-memory cache — the
    /// series counterpart to <see cref="AniSkipService"/> (and a gap-filler for anime that
    /// have an IMDb mapping). Skip times are crowdsourced and rarely change, so a 7-day TTL
    /// avoids hammering the upstream on a binge. The API is public/keyless; an optional key
    /// (X-API-Key) raises rate limits but is not required.
    /// </summary>
    public class IntroDbService : IIntroDbService
    {
        private const string IntroDbApi = "https://api.introdb.app";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

        // Static so the singleton's cache lives for the process. Negative results (no data)
        // are cached as empty lists so we don't keep re-hitting introdb for an episode it
        // genuinely has no markers for.
        private static readonly ConcurrentDictionary<string, (DateTime Expires, List<SkipTime> Data)> _cache = new();

        private readonly IHttpClientFactory _clientFactory;
        private readonly string _apiKey;

        public IntroDbService(IHttpClientFactory clientFactory, IConfiguration configuration)
        {
            _clientFactory = clientFactory;
            // Optional — the endpoint works without it. Accept the env-style INTRODB_API_KEY
            // or the appsettings IntroDbApiKey.
            _apiKey = configuration["INTRODB_API_KEY"] ?? configuration["IntroDbApiKey"] ?? "";
        }

        public async Task<List<SkipTime>> GetSkipTimesAsync(string imdbId, int season, int episode)
        {
            if (string.IsNullOrWhiteSpace(imdbId) || episode <= 0) return [];

            var key = $"{imdbId}:{season}:{episode}";
            if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.Expires)
                return cached.Data;

            // No segment_type → the endpoint returns the full { intro, recap, outro } object.
            var url = $"{IntroDbApi}/segments?imdb_id={Uri.EscapeDataString(imdbId)}&season={season}&episode={episode}";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(_apiKey)) request.Headers.Add("X-API-Key", _apiKey);
                var response = await _clientFactory.CreateClient().SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    // 4xx/5xx — don't poison the 7-day cache with an empty list; retry next time.
                    return [];
                }

                var content = await response.Content.ReadAsStringAsync();
                var data = ParseEpisode(content);
                _cache[key] = (DateTime.UtcNow + CacheTtl, data);
                return data;
            }
            catch
            {
                // Network blip / DNS / TLS — best-effort: no markers, but don't cache so we retry.
                return [];
            }
        }

        // introdb returns an object keyed by band: { imdb_id, season, episode,
        // "intro": { start_sec, end_sec, start_ms, end_ms, ... } | null, "recap": …, "outro": … }.
        // Map intro→op, recap→recap, outro→ed onto the AniSkip vocabulary. Falls back to a
        // segments[] array shape if the envelope ever differs.
        private static List<SkipTime> ParseEpisode(string content)
        {
            var data = new List<SkipTime>();
            if (string.IsNullOrWhiteSpace(content)) return data;

            JToken root;
            try { root = JToken.Parse(content); }
            catch { return data; }

            if (root is JObject obj)
            {
                TryAddBand(data, "op", obj["intro"]);
                TryAddBand(data, "recap", obj["recap"]);
                TryAddBand(data, "ed", obj["outro"]);
                if (data.Count > 0) return data;

                // Defensive fallback: a wrapped array of typed segments.
                var arr = obj["segments"] as JArray ?? obj["results"] as JArray ?? obj["data"] as JArray;
                if (arr is not null) AddFromArray(data, arr);
            }
            else if (root is JArray topArr)
            {
                AddFromArray(data, topArr);
            }
            return data;
        }

        // Read one band object ({ start_sec/end_sec, or start_ms/end_ms }); ignores null/missing.
        private static void TryAddBand(List<SkipTime> data, string type, JToken? node)
        {
            if (node is not JObject o) return;

            double? Sec(string secKey, string msKey)
            {
                var s = (double?)o[secKey];
                if (s.HasValue) return s;
                var ms = (double?)o[msKey];
                return ms.HasValue ? ms.Value / 1000.0 : (double?)null;
            }

            var start = Sec("start_sec", "start_ms");
            var end = Sec("end_sec", "end_ms");
            if (!start.HasValue || !end.HasValue || end.Value <= start.Value) return;
            data.Add(new SkipTime { Type = type, Start = start.Value, End = end.Value });
        }

        private static void AddFromArray(List<SkipTime> data, JArray arr)
        {
            foreach (var seg in arr.OfType<JObject>())
            {
                var raw = ((string)seg["segment_type"] ?? (string)seg["type"])?.Trim().ToLowerInvariant();
                var type = NormaliseType(raw);
                if (type is null) continue;
                var start = (double?)(seg["start_sec"] ?? seg["start"]);
                var end = (double?)(seg["end_sec"] ?? seg["end"]);
                if (!start.HasValue || !end.HasValue) continue;
                data.Add(new SkipTime { Type = type, Start = start.Value, End = end.Value });
            }
        }

        private static string? NormaliseType(string? t) => t switch
        {
            "intro" or "opening" or "op" => "op",
            "recap" or "previously" => "recap",
            "outro" or "credits" or "ending" or "ed" => "ed",
            _ => null,
        };
    }
}
