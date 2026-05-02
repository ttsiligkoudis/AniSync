using AnimeList.Models;
using AnimeList.Services.Interfaces;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Text;

namespace AnimeList
{
    public static class Utils
    {
        public static readonly string anilistPrefix = "anilist:";
        public static readonly string imdbPrefix = "tt";
        public static readonly string kitsuPrefix = "kitsu:";
        public static readonly string tmdbPrefix = "tmdb:";
        public static readonly string malPrefix = "mal:";

        public static readonly string DefaultOption = "None";
        public const string SeasonCurrent = "This Season";
        public const string SeasonNext = "Next Season";
        public const string SeasonPrevious = "Last Season";

        public static readonly List<string> SeasonOptions = [SeasonCurrent, SeasonNext, SeasonPrevious];

        public const string SortPopularity = "Popularity";
        public const string SortScore = "Score";
        public const string SortRecent = "Recent";

        public static readonly List<string> SortOptions = [SortPopularity, SortScore, SortRecent];

        /// <summary>
        /// Maps a UI sort label to the AniList <c>MediaSort</c> enum value.
        /// </summary>
        public static string SortToAnilist(string sort) => sort switch
        {
            SortScore => "SCORE_DESC",
            SortRecent => "START_DATE_DESC",
            _ => "POPULARITY_DESC",
        };

        /// <summary>
        /// Maps a UI sort label to the Kitsu <c>sort</c> query parameter value.
        /// </summary>
        public static string SortToKitsu(string sort) => sort switch
        {
            SortScore => "-averageRating",
            SortRecent => "-startDate",
            _ => "-userCount",
        };

        public static List<string> GetOptions(bool includeDefault) 
        {
            var options = new List<string>{
                "Action", "Adventure", "Comedy", "Drama", "Ecchi", "Fantasy",
                "Horror", "Mahou Shoujo", "Mecha", "Music", "Mystery",
                "Psychological", "Romance", "Sci-Fi", "Slice of Life",
                "Sports", "Supernatural", "Thriller" 
            };
            if (includeDefault) options.Insert(0, "None");
            return options;
        }

        /// <summary>
        /// Returns true when the format/subtype represents a movie (standalone, no episodes).
        /// </summary>
        public static bool IsMovieFormat(string format) =>
            !string.IsNullOrEmpty(format) && (format.Equals("MOVIE", StringComparison.OrdinalIgnoreCase)
                                              || format.Equals("SPECIAL", StringComparison.OrdinalIgnoreCase)
                                              || format.Equals("MUSIC", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Returns the AniList MediaSeason string and year for the given season option.
        /// </summary>
        public static (string Season, int Year) GetSeasonAndYear(string seasonOption)
        {
            var now = DateTime.UtcNow;
            int offset = seasonOption switch
            {
                SeasonNext => 1,
                SeasonPrevious => -1,
                _ => 0
            };

            int seasonIndex = (now.Month - 1) / 3; // 0=Winter, 1=Spring, 2=Summer, 3=Fall
            int targetIndex = seasonIndex + offset;
            int year = now.Year;

            if (targetIndex > 3)
            {
                targetIndex -= 4;
                year++;
            }
            else if (targetIndex < 0)
            {
                targetIndex += 4;
                year--;
            }

            string season = targetIndex switch
            {
                0 => "WINTER",
                1 => "SPRING",
                2 => "SUMMER",
                3 => "FALL",
                _ => "WINTER"
            };

            return (season, year);
        }

#if DEBUG
        public static readonly string clientId = "20853";
        public static readonly string clientSecret = "za9WKI03QY3icX3S4EvsSUuE0VB1b5MZelcT2S8m";
        public static readonly string redirectUri = "https://tools.myportofolio.eu/Auth/Callback";
#else
        public static readonly string clientId = "20850";
        public static readonly string clientSecret = "bAgns7Q0rGxXnhGRRoq84slYleN4NIe2SkoSDOZ1";
        public static readonly string redirectUri = "https://anisync.fly.dev/Auth/Callback";
#endif

        public static bool IsTokenExpired(DateTime? expirationDate)
        {
            return DateTime.UtcNow >= (expirationDate ?? DateTime.UtcNow).AddMinutes(-5);
        }

        public static List<Meta> ExpiredMetas()
        {
            return [new Meta { id = $"{anilistPrefix}token-expired", name = "Token expired, re-install addon" }];
        }

        /// <summary>
        /// Parses a Stremio video ID into its anime base ID + optional season + optional episode.
        /// Supports IMDb (tt12345 or tt12345:1:5) and prefixed (kitsu:12345, anilist:..., tmdb:..., optionally ":S:E") forms.
        /// </summary>
        public static bool TryParseAnimeId(string id, out string animeId, out int? season, out int? episode)
        {
            animeId = null;
            season = null;
            episode = null;
            if (string.IsNullOrEmpty(id)) return false;

            var parts = id.Split(':');

            if (id.StartsWith(imdbPrefix))
            {
                animeId = parts[0];
            }
            else if ((id.StartsWith(kitsuPrefix) || id.StartsWith(anilistPrefix)
                      || id.StartsWith(tmdbPrefix) || id.StartsWith(malPrefix))
                && parts.Length >= 2)
            {
                animeId = $"{parts[0]}:{parts[1]}";
            }
            else
            {
                return false;
            }

            if (parts.Length >= 3
                && int.TryParse(parts[^2], out var s)
                && int.TryParse(parts[^1], out var e))
            {
                season = s;
                episode = e;
            }

            return true;
        }

        public static string CompressString(string text)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(text);
            using var memoryStream = new MemoryStream();
            using (var gzipStream = new GZipStream(memoryStream, CompressionMode.Compress, true))
            {
                gzipStream.Write(buffer, 0, buffer.Length);
            }
            memoryStream.Position = 0;
            byte[] compressed = new byte[memoryStream.Length];
            memoryStream.Read(compressed, 0, compressed.Length);

            byte[] gzBuffer = new byte[compressed.Length + 4];
            Buffer.BlockCopy(compressed, 0, gzBuffer, 4, compressed.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(buffer.Length), 0, gzBuffer, 0, 4);
            return Convert.ToBase64String(gzBuffer);
        }

        public static string DecompressString(string compressedText)
        {
            byte[] gzBuffer = Convert.FromBase64String(compressedText);
            using var memoryStream = new MemoryStream();
            int dataLength = BitConverter.ToInt32(gzBuffer, 0);
            memoryStream.Write(gzBuffer, 4, gzBuffer.Length - 4);

            byte[] buffer = new byte[dataLength];
            memoryStream.Position = 0;

            using (var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress))
            {
                gzipStream.Read(buffer, 0, buffer.Length);
            }
            return Encoding.UTF8.GetString(buffer);
        }

        /// <summary>
        /// Compresses text using GZip and returns a URL-safe Base64 string (no padding, +→-, /→_).
        /// </summary>
        public static string CompressToUrlSafe(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress))
            {
                gzip.Write(data, 0, data.Length);
            }
            return Base64UrlEncode(output.ToArray());
        }

        /// <summary>
        /// Decompresses a URL-safe Base64 GZip string back to the original text.
        /// </summary>
        public static string DecompressFromUrlSafe(string compressedText)
        {
            byte[] data = Base64UrlDecode(compressedText);
            return DecompressBytes(data);
        }

        /// <summary>
        /// Encodes raw bytes as a URL-safe Base64 string (no padding, +→-, /→_).
        /// </summary>
        public static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        /// <summary>
        /// Decodes a URL-safe Base64 string back to raw bytes.
        /// </summary>
        public static byte[] Base64UrlDecode(string text)
        {
            string base64 = text.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Convert.FromBase64String(base64);
        }

        private static string DecompressBytes(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }

        /// <summary>
        /// Decodes a config route parameter into a <see cref="Configuration"/>.
        /// Supports seven formats for backward compatibility:
        /// <list type="bullet">
        ///   <item>Legacy raw JSON (starts with '{')</item>
        ///   <item>GZip-compressed JSON via Base64Url (GZip magic bytes 0x1F 0x8B)</item>
        ///   <item>Binary v1: [0x01][flags byte][GZip tokenData] — 8 catalog flags</item>
        ///   <item>Binary v2: [0x02][flags1][flags2][GZip tokenData] — 16 catalog flags</item>
        ///   <item>Binary v3: [0x03][flags1][flags2][flags3][GZip tokenData] — 24 catalog flags</item>
        ///   <item>Binary v4: [0x04][flags1][flags2][flags3][16-byte UID] — flags in the URL,
        ///     token JSON in the config store. Decoded for back-compat; new installs use v5.</item>
        ///   <item>Binary v5: [0x05][16-byte UID] — UID only; both flags and token JSON live
        ///     in the config store. Lets the configure-page "Save" button persist toggle
        ///     changes server-side without forcing a reinstall in Stremio.</item>
        /// </list>
        /// For v5, the flag fields on the returned <see cref="Configuration"/> stay at their
        /// default (false) — call <see cref="ResolveConfigAsync"/> to hydrate them from the
        /// config store, or check <see cref="Configuration.flagsInDb"/> and do it yourself.
        /// </summary>
        public static Configuration DecodeConfig(string config)
        {
            if (string.IsNullOrEmpty(config))
                return null;

            // Legacy format: raw JSON starts with '{'
            if (config.StartsWith('{'))
            {
                var result = DeserializeObject<Configuration>(config);
                if (!string.IsNullOrEmpty(result?.tokenData))
                    result.tokenData = DecompressString(Uri.UnescapeDataString(result.tokenData));
                return result;
            }

            byte[] data = Base64UrlDecode(config);

            // Previous format: GZip-compressed JSON (magic bytes 0x1F 0x8B)
            if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B)
            {
                string json = DecompressBytes(data);
                var result = DeserializeObject<Configuration>(json);
                if (!string.IsNullOrEmpty(result?.tokenData))
                    result.tokenData = DecompressString(Uri.UnescapeDataString(result.tokenData));
                return result;
            }

            // Binary v1: [0x01][flags][GZip tokenData]
            if (data.Length >= 2 && data[0] == 0x01)
                return DecodeBinaryConfig(data, headerLen: 2, flags1: data[1], flags2: 0, flags3: 0);

            // Binary v2: [0x02][flags1][flags2][GZip tokenData]
            if (data.Length >= 3 && data[0] == 0x02)
                return DecodeBinaryConfig(data, headerLen: 3, flags1: data[1], flags2: data[2], flags3: 0);

            // Binary v3: [0x03][flags1][flags2][flags3][GZip tokenData]
            if (data.Length >= 4 && data[0] == 0x03)
                return DecodeBinaryConfig(data, headerLen: 4, flags1: data[1], flags2: data[2], flags3: data[3]);

            // Binary v4: [0x04][flags1][flags2][flags3][16-byte UID]
            if (data.Length >= 4 + 16 && data[0] == 0x04)
            {
                var cfg = DecodeBinaryFlags(data[1], data[2], data[3]);
                cfg.tokenUid = Base64UrlEncode(data[4..(4 + 16)]);
                return cfg;
            }

            // Binary v5: [0x05][16-byte UID] — flags persisted in the config store
            if (data.Length >= 1 + 16 && data[0] == 0x05)
            {
                return new Configuration
                {
                    tokenUid = Base64UrlEncode(data[1..(1 + 16)]),
                    flagsInDb = true,
                };
            }

            throw new ArgumentException("Unknown config format");
        }

        /// <summary>
        /// Calls <see cref="DecodeConfig"/> and, for v5 URLs, hydrates the toggle flags from
        /// the config store. Use this from controllers that need the flag bits (Manifest,
        /// Stream, Home). v3/v4 paths never touch the store.
        /// </summary>
        public static async Task<Configuration> ResolveConfigAsync(string config, IConfigStore store)
        {
            var cfg = DecodeConfig(config);
            if (cfg?.flagsInDb == true && !string.IsNullOrEmpty(cfg.tokenUid))
            {
                var (f1, f2, f3, _) = await store.GetFlagsAsync(cfg.tokenUid);
                ApplyBinaryFlags(cfg, f1, f2, f3);
            }
            return cfg;
        }

        /// <summary>
        /// Writes flag bits into the existing <see cref="Configuration"/> instance. Used by
        /// both <see cref="DecodeBinaryFlags"/> (URL bytes path) and <see cref="ResolveConfigAsync"/>
        /// (DB-backed path) so the bit layout stays in one place.
        /// </summary>
        public static void ApplyBinaryFlags(Configuration cfg, byte flags1, byte flags2, byte flags3)
        {
            cfg.showCurrent = (flags1 & 0x01) != 0;
            cfg.showCompleted = (flags1 & 0x02) != 0;
            cfg.showTrending = (flags1 & 0x04) != 0;
            cfg.showSeasonal = (flags1 & 0x08) != 0;
            cfg.discoverOnlyCurrent = (flags1 & 0x10) != 0;
            cfg.discoverOnlyCompleted = (flags1 & 0x20) != 0;
            cfg.discoverOnlyTrending = (flags1 & 0x40) != 0;
            cfg.discoverOnlySeasonal = (flags1 & 0x80) != 0;
            cfg.showPlanning = (flags2 & 0x01) != 0;
            cfg.showPaused = (flags2 & 0x02) != 0;
            cfg.showDropped = (flags2 & 0x04) != 0;
            cfg.showRepeating = (flags2 & 0x08) != 0;
            cfg.discoverOnlyPlanning = (flags2 & 0x10) != 0;
            cfg.discoverOnlyPaused = (flags2 & 0x20) != 0;
            cfg.discoverOnlyDropped = (flags2 & 0x40) != 0;
            cfg.discoverOnlyRepeating = (flags2 & 0x80) != 0;
            cfg.showAiring = (flags3 & 0x01) != 0;
            cfg.showExternalStreams = (flags3 & 0x02) != 0;
            cfg.discoverOnlyAiring = (flags3 & 0x10) != 0;
        }

        private static Configuration DecodeBinaryConfig(byte[] data, int headerLen, byte flags1, byte flags2, byte flags3)
        {
            var cfg = DecodeBinaryFlags(flags1, flags2, flags3);
            cfg.tokenData = data.Length > headerLen ? DecompressBytes(data[headerLen..]) : null;
            return cfg;
        }

        private static Configuration DecodeBinaryFlags(byte flags1, byte flags2, byte flags3)
        {
            var cfg = new Configuration();
            ApplyBinaryFlags(cfg, flags1, flags2, flags3);
            return cfg;
        }

        /// <summary>
        /// Maps a <see cref="ListType"/> to the status/sort string the chosen service expects.
        /// Both services share most names; Kitsu renames a couple ("planned" / "on_hold") and
        /// has no equivalent of AniList's "REPEATING".
        /// </summary>
        public static string GetListTypeString(ListType list, TokenData tokenData)
        {
            var isKitsu = (tokenData?.anime_service ?? AnimeService.Kitsu) == AnimeService.Kitsu;
            if (!isKitsu) return list.ToString().ToUpper();

            return list switch
            {
                ListType.Planning => "planned",
                ListType.Paused => "on_hold",
                _ => list.ToString().ToLower(),
            };
        }

        /// <summary>
        /// Walks a JSON object along the given path, returning null at the first missing or null segment.
        /// Works on Newtonsoft <see cref="JToken"/> trees (the runtime type of <c>DeserializeObject&lt;dynamic&gt;</c>).
        /// </summary>
        public static JToken SafeGet(JToken token, params string[] path)
        {
            var current = token;
            foreach (var part in path)
            {
                if (current == null || current.Type == JTokenType.Null) return null;
                current = current[part];
            }
            return current is { Type: JTokenType.Null } ? null : current;
        }

        /// <summary>
        /// Like <see cref="SafeGet(JToken, string[])"/>, but converts the leaf to <typeparamref name="T"/>
        /// (returns <c>default</c> if the path is missing or the conversion fails).
        /// </summary>
        public static T SafeGet<T>(JToken token, params string[] path)
        {
            var result = SafeGet(token, path);
            if (result == null) return default;
            try { return result.ToObject<T>(); }
            catch { return default; }
        }
    }
}
