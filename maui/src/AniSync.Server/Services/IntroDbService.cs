using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace AnimeList.Services
{
    /// <summary>
    /// introdb.app client with a per-(IMDb id, season, episode) in-memory cache — the
    /// series counterpart to <see cref="AniSkipService"/>. Skip times are crowdsourced and
    /// rarely change, so a 7-day TTL avoids hammering the upstream on a binge. No-ops
    /// (returns empty) when no API key is configured, so the feature is opt-in by deployment.
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
            // Accept either the env-style INTRODB_API_KEY or the appsettings IntroDbApiKey.
            _apiKey = configuration["INTRODB_API_KEY"] ?? configuration["IntroDbApiKey"] ?? "";
        }

        public async Task<List<SkipTime>> GetSkipTimesAsync(string imdbId, int season, int episode)
        {
            // No key ⇒ feature disabled for this deployment. No network call, no cache.
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(imdbId) || episode <= 0)
                return [];

            var key = $"{imdbId}:{season}:{episode}";
            if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.Expires)
                return cached.Data;

            var url = $"{IntroDbApi}/segments?imdb_id={Uri.EscapeDataString(imdbId)}&season={season}&episode={episode}";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-API-Key", _apiKey);
                var response = await _clientFactory.CreateClient().SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    // 4xx/5xx — don't poison the 7-day cache with an empty list; retry next time.
                    return [];
                }

                var content = await response.Content.ReadAsStringAsync();
                var data = ParseSegments(content);
                _cache[key] = (DateTime.UtcNow + CacheTtl, data);
                return data;
            }
            catch
            {
                // Network blip / DNS / TLS — best-effort: no markers, but don't cache so we retry.
                return [];
            }
        }

        // Defensive parse — the exact envelope isn't documented publicly. Accept a bare array
        // or a { segments|results|data: [...] } wrapper. Each segment: segment_type (+ start_sec
        // / end_sec). segment_type is normalised onto the AniSkip vocabulary so the bootstrap
        // maps introdb and AniSkip identically.
        private static List<SkipTime> ParseSegments(string content)
        {
            var data = new List<SkipTime>();
            if (string.IsNullOrWhiteSpace(content)) return data;

            JToken root;
            try { root = JToken.Parse(content); }
            catch { return data; }

            var arr = root as JArray
                ?? root["segments"] as JArray
                ?? root["results"] as JArray
                ?? root["data"] as JArray;
            if (arr is null) return data;

            foreach (var seg in arr.OfType<JObject>())
            {
                var rawType = ((string)seg["segment_type"] ?? (string)seg["type"])?.Trim().ToLowerInvariant();
                var start = (double?)(seg["start_sec"] ?? seg["start"] ?? seg["startTime"]);
                var end = (double?)(seg["end_sec"] ?? seg["end"] ?? seg["endTime"]);
                if (string.IsNullOrEmpty(rawType) || !start.HasValue || !end.HasValue) continue;

                var type = NormaliseType(rawType);
                if (type is null) continue;
                data.Add(new SkipTime { Type = type, Start = start.Value, End = end.Value });
            }
            return data;
        }

        // Map introdb segment types onto the AniSkip set the bootstrap already understands.
        private static string? NormaliseType(string t) => t switch
        {
            "intro" or "opening" or "op" => "op",
            "recap" or "previously" => "recap",
            "outro" or "credits" or "ending" or "ed" => "ed",
            _ => null,   // ignore preview/unknown
        };
    }
}
