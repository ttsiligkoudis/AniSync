using Newtonsoft.Json;

namespace AnimeList.Models
{
    /// <summary>
    /// Webhook payload shapes for Plex, Jellyfin, and Emby. Only the fields the scrobble
    /// pipeline actually reads are modelled — every server sends a lot more than we need
    /// (UI hints, server identity, agent metadata, library context, …) and ignoring the
    /// rest keeps the DTOs honest about what we depend on.
    ///
    /// The three formats normalise into <see cref="NormalizedScrobbleEvent"/> before any
    /// business logic runs, so the controller doesn't have three branches downstream of
    /// the parse step.
    /// </summary>

    // ── Plex (multipart/form-data, JSON in the "payload" field) ─────────────
    // Reference payload: https://support.plex.tv/articles/115002267687-webhooks/
    public sealed class PlexWebhook
    {
        [JsonProperty("event")] public string Event { get; set; }
        [JsonProperty("Account")] public PlexAccount Account { get; set; }
        [JsonProperty("Metadata")] public PlexMetadata Metadata { get; set; }
    }

    public sealed class PlexAccount
    {
        [JsonProperty("title")] public string Title { get; set; }
    }

    public sealed class PlexMetadata
    {
        [JsonProperty("type")] public string Type { get; set; }                    // "episode" | "movie" | "track" | …
        [JsonProperty("grandparentTitle")] public string GrandparentTitle { get; set; } // series title for episodes
        [JsonProperty("parentIndex")] public int? ParentIndex { get; set; }        // season number
        [JsonProperty("index")] public int? Index { get; set; }                    // episode number
        [JsonProperty("Guid")] public List<PlexGuid> Guids { get; set; }           // external ids ("imdb://ttX", "tvdb://N", …)
    }

    public sealed class PlexGuid
    {
        [JsonProperty("id")] public string Id { get; set; }
    }

    // ── Jellyfin (application/json, fields directly on the root object) ──────
    // Reference: jellyfin-plugin-webhook "Generic" template — fields are flat.
    public sealed class JellyfinWebhook
    {
        [JsonProperty("NotificationType")] public string NotificationType { get; set; }
        [JsonProperty("UserId")] public string UserId { get; set; }
        [JsonProperty("NotificationUsername")] public string NotificationUsername { get; set; }
        [JsonProperty("ItemType")] public string ItemType { get; set; }            // "Episode" | "Movie" | …
        [JsonProperty("SeriesName")] public string SeriesName { get; set; }
        [JsonProperty("SeasonNumber")] public int? SeasonNumber { get; set; }
        [JsonProperty("EpisodeNumber")] public int? EpisodeNumber { get; set; }
        [JsonProperty("PlayedToCompletion")] public bool? PlayedToCompletion { get; set; }
        [JsonProperty("Provider_imdb")] public string ProviderImdb { get; set; }
        [JsonProperty("Provider_tvdb")] public string ProviderTvdb { get; set; }
        [JsonProperty("Provider_tmdb")] public string ProviderTmdb { get; set; }
        [JsonProperty("Provider_anidb")] public string ProviderAnidb { get; set; }
    }

    // ── Emby (application/json, similar to Plex but flatter) ─────────────────
    // Reference: https://github.com/MediaBrowser/Wiki/wiki/Webhooks
    public sealed class EmbyWebhook
    {
        [JsonProperty("Event")] public string Event { get; set; }
        [JsonProperty("User")] public EmbyUser User { get; set; }
        [JsonProperty("Item")] public EmbyItem Item { get; set; }
    }

    public sealed class EmbyUser
    {
        [JsonProperty("Name")] public string Name { get; set; }
    }

    public sealed class EmbyItem
    {
        [JsonProperty("Type")] public string Type { get; set; }                    // "Episode" | "Movie" | …
        [JsonProperty("SeriesName")] public string SeriesName { get; set; }
        [JsonProperty("ParentIndexNumber")] public int? ParentIndexNumber { get; set; }
        [JsonProperty("IndexNumber")] public int? IndexNumber { get; set; }
        [JsonProperty("ProviderIds")] public Dictionary<string, string> ProviderIds { get; set; }
    }

    /// <summary>
    /// Server-agnostic scrobble event the pipeline operates on. Built by the per-source
    /// parser; the rest of the pipeline (filters, mapping, dedup, fan-out) only ever
    /// sees this shape.
    /// </summary>
    public sealed class NormalizedScrobbleEvent
    {
        public string Source { get; init; }                    // "plex" | "jellyfin" | "emby"
        public bool IsScrobble { get; init; }                  // event type signals "watched"
        public bool IsEpisode { get; init; }                   // skip movies/music/etc for v1
        public string Username { get; init; }                  // for the Plex Home filter
        public string SeriesTitle { get; init; }
        public int? Season { get; init; }
        public int? Episode { get; init; }
        public List<(string prefix, string id)> ExternalIds { get; init; } = new();
    }
}
