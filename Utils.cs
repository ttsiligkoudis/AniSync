using AnimeList.Models;
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

        public static readonly string DefaultOption = "None";
        public const string SeasonCurrent = "This Season";
        public const string SeasonNext = "Next Season";
        public const string SeasonPrevious = "Last Season";

        public static readonly List<string> SeasonOptions = [SeasonCurrent, SeasonNext, SeasonPrevious];

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
            else if ((id.StartsWith(kitsuPrefix) || id.StartsWith(anilistPrefix) || id.StartsWith(tmdbPrefix))
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
        /// Supports three formats for backward compatibility:
        /// <list type="bullet">
        ///   <item>Legacy raw JSON (starts with '{')</item>
        ///   <item>GZip-compressed JSON via Base64Url (GZip magic bytes 0x1F 0x8B)</item>
        ///   <item>Binary v1: [0x01][flags bitmask][GZip-compressed tokenData bytes]</item>
        /// </list>
        /// The returned <see cref="Configuration.tokenData"/> is always raw token JSON.
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

            // Binary v1 format: [0x01][flags][GZip tokenData bytes...]
            if (data.Length >= 2 && data[0] == 0x01)
            {
                byte flags = data[1];
                string tokenJson = data.Length > 2
                    ? DecompressBytes(data[2..])
                    : null;

                return new Configuration
                {
                    tokenData = tokenJson,
                    showCurrent = (flags & 0x01) != 0,
                    showCompleted = (flags & 0x02) != 0,
                    showTrending = (flags & 0x04) != 0,
                    showSeasonal = (flags & 0x08) != 0,
                    discoverOnlyCurrent = (flags & 0x10) != 0,
                    discoverOnlyCompleted = (flags & 0x20) != 0,
                    discoverOnlyTrending = (flags & 0x40) != 0,
                    discoverOnlySeasonal = (flags & 0x80) != 0,
                };
            }

            throw new ArgumentException("Unknown config format");
        }

        public static string GetListTypeString(ListType list, TokenData tokenData)
        {
            return (tokenData?.anime_service ?? AnimeService.Kitsu) == AnimeService.Kitsu
                ? list.ToString().ToLower()
                : list.ToString().ToUpper();
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
