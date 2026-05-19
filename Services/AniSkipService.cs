using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace AnimeList.Services
{
    /// <summary>
    /// AniSkip v2 client with a per-(MAL id, episode) in-memory cache. Skip times are
    /// crowdsourced and rarely change after submission, so a 7-day TTL is plenty —
    /// avoids hammering the upstream on every Stream request for a binge-watcher.
    /// </summary>
    public class AniSkipService : IAniSkipService
    {
        private const string AniSkipApi = "https://api.aniskip.com/v2";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

        // Static so the singleton service's cache persists across the lifetime of the
        // process. Negative results (no skip data found) are cached as empty lists so
        // we don't keep retrying every time the user opens an episode that AniSkip
        // genuinely doesn't have markers for.
        private static readonly ConcurrentDictionary<string, (DateTime Expires, List<SkipTime> Data)> _cache = new();

        private readonly IHttpClientFactory _clientFactory;

        public AniSkipService(IHttpClientFactory clientFactory)
        {
            _clientFactory = clientFactory;
        }

        public async Task<List<SkipTime>> GetSkipTimesAsync(int malId, int episode)
        {
            if (malId <= 0 || episode <= 0) return [];

            var key = $"{malId}:{episode}";
            if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.Expires)
                return cached.Data;

            // AniSkip v2 expects the array form `types[]=…` and a
            // mandatory `episodeLength` (seconds). We don't know the
            // runtime until the user picks a source, but the API
            // accepts 0 — used in the same way the official player
            // extension does — and still returns the markers.
            var url = $"{AniSkipApi}/skip-times/{malId}/{episode}"
                + "?types[]=op&types[]=ed&types[]=mixed-op&types[]=mixed-ed&types[]=recap&types[]=preview"
                + "&episodeLength=0";

            var data = new List<SkipTime>();
            try
            {
                var response = await _clientFactory.CreateClient().GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var json = JObject.Parse(content);
                    if ((bool?)json["found"] == true && json["results"] is JArray results)
                    {
                        foreach (var r in results.OfType<JObject>())
                        {
                            var skipType = (string)r["skipType"];
                            var startTime = (double?)r["interval"]?["startTime"];
                            var endTime = (double?)r["interval"]?["endTime"];
                            if (string.IsNullOrEmpty(skipType) || !startTime.HasValue || !endTime.HasValue) continue;
                            data.Add(new SkipTime { Type = skipType, Start = startTime.Value, End = endTime.Value });
                        }
                    }
                }
            }
            catch
            {
                // AniSkip is best-effort — a transient network blip just means no markers
                // for this view. Don't poison the cache with a missing result though;
                // skip the cache write so the next request can retry.
                return data;
            }

            _cache[key] = (DateTime.UtcNow + CacheTtl, data);
            return data;
        }
    }
}
