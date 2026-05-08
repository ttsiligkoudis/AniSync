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

        /// <summary>
        /// Maps an <see cref="AnimeService"/> to its catalog id prefix. Used by
        /// controllers / fallback services that need to stamp an id with the
        /// caller's prefix when bridging across services.
        /// </summary>
        public static string GetServicePrefix(AnimeService service) => service switch
        {
            AnimeService.Anilist => anilistPrefix,
            AnimeService.Kitsu => kitsuPrefix,
            AnimeService.MyAnimeList => malPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(service)),
        };

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

        /// <summary>
        /// Maps a UI sort label to a MyAnimeList <c>ranking_type</c> value.
        /// MAL's anime ranking endpoint exposes a fixed set of buckets; the UI sort
        /// labels are translated to the closest matching one.
        /// </summary>
        public static string SortToMal(string sort) => sort switch
        {
            SortScore => "all",
            SortRecent => "airing",
            _ => "bypopularity",
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

        /// <summary>
        /// Generates a PKCE code-verifier — 64 random bytes, base64url-encoded. MAL only
        /// supports <c>code_challenge_method=plain</c>, so the same string is sent as the
        /// challenge in the authorize step and as the verifier in the token-exchange step.
        /// </summary>
        public static string GenerateCodeVerifier()
        {
            var bytes = new byte[64];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return Base64UrlEncode(bytes);
        }

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
            cfg.hideManageEntry = (flags3 & 0x04) != 0;
            cfg.disableAutoTrack = (flags3 & 0x08) != 0;
            cfg.discoverOnlyAiring = (flags3 & 0x10) != 0;
            cfg.disableSeasonGrouping = (flags3 & 0x20) != 0;
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
        /// AniList uses uppercase enum values ("CURRENT", "REPEATING"). Kitsu uses lowercase
        /// with two renames ("planned" / "on_hold") and has no "Repeating". MAL uses the same
        /// shape as Kitsu but with "watching" / "plan_to_watch" instead, and treats rewatching
        /// as a per-entry boolean rather than a separate list — so we still emit "watching"
        /// for ListType.Repeating and let the caller filter on is_rewatching.
        /// </summary>
        public static string GetListTypeString(ListType list, TokenData tokenData)
        {
            var service = tokenData?.anime_service ?? AnimeService.Kitsu;
            if (service == AnimeService.Anilist) return list.ToString().ToUpper();

            if (service == AnimeService.MyAnimeList)
            {
                return list switch
                {
                    ListType.Current => "watching",
                    ListType.Repeating => "watching", // filter-side: is_rewatching=true
                    ListType.Planning => "plan_to_watch",
                    ListType.Paused => "on_hold",
                    _ => list.ToString().ToLower(),
                };
            }

            // Kitsu
            return list switch
            {
                ListType.Planning => "planned",
                ListType.Paused => "on_hold",
                _ => list.ToString().ToLower(),
            };
        }

        /// <summary>
        /// Walks a JSON object along the given path, returning null at the first missing,
        /// JSON-null, or non-object segment. Newtonsoft's <see cref="JToken"/> indexer throws
        /// when invoked on a <see cref="JValue"/>, so we gate every step on <c>is JObject</c>
        /// rather than just null-checking.
        /// </summary>
        public static JToken SafeGet(JToken token, params string[] path)
        {
            var current = token;
            foreach (var part in path)
            {
                if (current is not JObject obj) return null;
                current = obj[part];
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

        public static bool IsValidUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri result)
                   && (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Collapses every per-provider list-status spelling to a single canonical
        /// lowercase name. Lets a caller compare AniList's "CURRENT", Kitsu's
        /// "current" and MAL's "watching" against each other without re-implementing
        /// the per-vocabulary mapping. Returns null for unknown / empty inputs.
        /// </summary>
        public static string NormalizeListStatus(string raw) => raw?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "watching" or "current" => "watching",
            "completed" => "completed",
            "planning" or "planned" or "plan_to_watch" or "plantowatch" => "planning",
            "paused" or "on_hold" or "onhold" => "paused",
            "dropped" => "dropped",
            "rewatching" or "repeating" => "rewatching",
            _ => null,
        };

        /// <summary>
        /// Translates any canonical / friendly status name (<c>watching</c>,
        /// <c>completed</c>, <c>plan_to_watch</c>, …) into the raw form the given
        /// service's save endpoint actually accepts. Pass-through when the input
        /// is already a known per-service value or doesn't normalise.
        ///
        /// Useful at the API write boundary so external callers can use one
        /// vocabulary regardless of which provider is primary on a given config.
        /// MAL's REPEATING is rendered as the synthetic <c>rewatching</c> sentinel
        /// that <c>MalService.SaveAnimeEntryAsync</c> turns into
        /// <c>status=watching, is_rewatching=true</c> on the wire; Kitsu has no
        /// rewatching concept so REPEATING degrades to <c>current</c>.
        /// </summary>
        public static string TranslateStatusForService(string status, TokenData tokenData)
        {
            if (string.IsNullOrEmpty(status)) return status;

            var canonical = NormalizeListStatus(status);
            if (canonical == null) return status; // unknown vocabulary — pass through.

            var listType = canonical switch
            {
                "watching" => ListType.Current,
                "completed" => ListType.Completed,
                "planning" => ListType.Planning,
                "paused" => ListType.Paused,
                "dropped" => ListType.Dropped,
                "rewatching" => ListType.Repeating,
                _ => (ListType?)null,
            };
            if (!listType.HasValue) return status;

            var service = tokenData?.anime_service ?? AnimeService.Kitsu;
            if (listType.Value == ListType.Repeating && service == AnimeService.MyAnimeList)
                return "rewatching";
            if (listType.Value == ListType.Repeating && service == AnimeService.Kitsu)
                return GetListTypeString(ListType.Current, tokenData);

            return GetListTypeString(listType.Value, tokenData);
        }
        /// <summary>
        /// Normalises a show title for fuzzy matching. Strips bracketed / parens
        /// content (<c>(Sub)</c>, <c>(Dub)</c>, <c>(2024)</c>, <c>[1080p]</c>),
        /// <c>Season N</c> / <c>Part N</c> / <c>S2</c> suffixes, punctuation, and
        /// collapses whitespace. Used by both the API <c>/match</c> endpoint and
        /// the Stremio search catalog so a query like "Bookworm Season 3" still
        /// scores against "Honzuki no Gekokujou: Adopted Daughter of an Archduke".
        /// </summary>
        public static string NormalizeTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.ToLowerInvariant();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\([^)]*\)", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\bseason\s*\d+\b", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\bpart\s*\d+\b", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\bs\d+\b", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9\s]", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        /// <summary>
        /// Title-similarity score between a query and a candidate. Blends two
        /// metrics:
        /// <list type="bullet">
        /// <item><b>Containment</b> (<c>intersect / |query|</c>) — how much of the
        /// query is covered by the candidate. Rewards specific titles that
        /// contain every query token, so a query like "Naruto Shippuden movie 2"
        /// scores "Naruto Shippuden the Movie 2: Bonds" higher than
        /// "Naruto Shippuden the Movie" (the latter is missing the "2").</item>
        /// <item><b>Jaccard</b> (<c>intersect / union</c>) — penalises candidates
        /// for extra noise. Breaks ties when multiple candidates contain every
        /// query token, e.g. "Naruto" beats "Boruto: Naruto Next Generations"
        /// for the query "Naruto" even though both score 1.0 on containment.</item>
        /// </list>
        /// Returns 1.0 on identical normalised strings and 0 on disjoint sets.
        /// </summary>
        public static double ScoreMatch(string normalisedQuery, string candidate)
        {
            if (string.IsNullOrEmpty(normalisedQuery) || string.IsNullOrEmpty(candidate)) return 0;
            var normalisedCandidate = NormalizeTitle(candidate);
            if (normalisedQuery == normalisedCandidate) return 1.0;
            var qTokens = normalisedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cTokens = normalisedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (qTokens.Length == 0 || cTokens.Length == 0) return 0;
            var qSet = new HashSet<string>(qTokens);
            var cSet = new HashSet<string>(cTokens);
            var intersect = qSet.Intersect(cSet).Count();
            if (intersect == 0) return 0;
            var union = qSet.Union(cSet).Count();
            var containment = (double)intersect / qSet.Count;
            var jaccard = (double)intersect / union;
            // Equal-weighted blend. Containment dominates for "is the query a subset
            // of this title", Jaccard breaks ties by penalising bloat. Tune the
            // weights up/down if one ranking signal proves more useful in practice.
            return 0.5 * containment + 0.5 * jaccard;
        }

        /// <summary>
        /// Rewrites every <see cref="Video.id"/> so it shares a prefix with the parent
        /// <see cref="Meta.id"/>. Stremio renders a blank meta page when the prefixes
        /// disagree, so this is essential after a Kitsu cross-service fallback in MAL /
        /// AniList that leaves <c>kitsu:N</c> ids in place. Defaults missing season /
        /// episode to 1 so the resulting id is always a valid 3- or 4-segment shape
        /// depending on whether the meta is grouped.
        /// </summary>
        /// <param name="videos">List to mutate in place.</param>
        /// <param name="externalId">The parent meta's id space — typically
        /// <c>tt12345</c> / <c>tmdb:N</c> when grouped, or <c>kitsu:N</c> /
        /// <c>anilist:N</c> / <c>mal:N</c> when not.</param>
        /// <param name="hasGroupId">True for grouped meta (id format
        /// <c>{externalId}:{season}:{episode}</c>); false for native single-cour ids
        /// (<c>{externalId}:{episode}</c>).</param>
        public static void NormalizeVideoIds(List<Video> videos, string externalId, bool hasGroupId)
        {
            foreach (var v in videos)
            {
                var season = v.season > 0 ? v.season : 1;
                var episode = v.episode > 0 ? v.episode : 1;
                v.id = hasGroupId ? $"{externalId}:{season}:{episode}" : $"{externalId}:{episode}";
                v.season = season;
                v.episode = episode;
            }
        }

        /// <summary>
        /// Lenient date parser used by per-provider list-entry hydration where the
        /// upstream returns either a full ISO timestamp or just <c>yyyy-MM-dd</c>.
        /// Returns null on empty / unparseable input rather than throwing — list
        /// metadata is best-effort and a missing date shouldn't fail a save.
        /// </summary>
        public static DateTime? ParseProviderDate(string raw) =>
            DateTime.TryParse(raw, out var dt) ? dt : null;

        /// <summary>
        /// Sets the <c>Authorization: Bearer &lt;token&gt;</c> header on a request
        /// when the supplied <see cref="TokenData"/> has a non-empty access token.
        /// No-op when the token is missing — matches the per-service convention of
        /// "fall through to anonymous client-id auth" for endpoints that allow it.
        /// </summary>
        public static void ApplyBearerAuth(HttpRequestMessage request, TokenData tokenData)
        {
            if (string.IsNullOrWhiteSpace(tokenData?.access_token)) return;
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", tokenData.access_token);
        }

        /// <summary>
        /// Picks the meta id and (separate) group id for a per-service GetAnimeByIdAsync
        /// response. Falls through IMDb → tmdb:N → optional kitsu:N → native id, in that
        /// order. The pair lets callers display a grouped meta (id == groupId) while still
        /// stamping non-grouped video ids with the native prefix when needed.
        /// </summary>
        /// <param name="mapping">Cross-service mapping row, may be null.</param>
        /// <param name="nativeId">Already-prefixed native id for the calling service
        /// (e.g. <c>"anilist:12345"</c>).</param>
        /// <param name="groupSeasons">When true, externalId == groupId so multiple cours
        /// of a franchise collapse to one card. When false, externalId stays in the native
        /// id space.</param>
        /// <param name="allowKitsuFallback">AniList/MAL set this to true so they can
        /// inherit a Kitsu id from the mapping when no IMDb/TMDB cross-mapping exists.
        /// Kitsu sets it to false — its native id IS a kitsu id, so the fallback is a no-op.</param>
        public static (string externalId, string groupId, bool hasGroupId) ResolveGroupedId(
            AnimeIdMapping mapping, string nativeId, bool groupSeasons, bool allowKitsuFallback)
        {
            var groupId = !string.IsNullOrEmpty(mapping?.ImdbId) ? mapping.ImdbId :
                          !string.IsNullOrEmpty(mapping?.TmdbId) ? $"{tmdbPrefix}{mapping.TmdbId}" : null;

            var hasGroupId = !string.IsNullOrEmpty(groupId);

            if (!hasGroupId)
                groupId = (allowKitsuFallback && mapping?.KitsuId.HasValue == true)
                    ? $"{kitsuPrefix}{mapping.KitsuId}"
                    : nativeId;

            var externalId = groupSeasons ? groupId : nativeId;
            return (externalId, groupId, hasGroupId);
        }

        /// <summary>
        /// Coalesces a 0-or-null score sentinel to null. AniList and MAL both treat
        /// score == 0 as "no rating", so a sparse hydration shouldn't carry it through.
        /// </summary>
        public static double? NullableScore(double? raw) =>
            (raw.HasValue && raw.Value > 0) ? raw : null;

        /// <summary>
        /// Translates a non-success <see cref="HttpResponseMessage"/> into the right
        /// exception shape for SyncService: <see cref="UnauthorizedAccessException"/>
        /// for 401/403 (so a stale linked-account token can be flagged NeedsReauth),
        /// <see cref="HttpRequestException"/> for everything else. Pass
        /// <paramref name="includeBody"/>=true on Kitsu writes so the 422 validation
        /// payload (<c>errors[].detail</c>) makes it into the message.
        /// </summary>
        public static async Task EnsureSuccessOrThrow(HttpResponseMessage response, string serviceName, string op, bool includeBody = false)
        {
            if (response.IsSuccessStatusCode) return;
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException($"{serviceName} {op} returned {(int)response.StatusCode}");

            var detail = "";
            if (includeBody)
            {
                string body = null;
                try { body = await response.Content.ReadAsStringAsync(); }
                catch { /* best-effort */ }
                if (!string.IsNullOrEmpty(body))
                    detail = $" — body: {(body.Length <= 600 ? body : body[..600] + "…")}";
            }
            throw new HttpRequestException($"{serviceName} {op} returned {(int)response.StatusCode}{detail}");
        }
    }
}
