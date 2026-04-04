using AnimeList.Models;
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

        public const string SeasonCurrent = "This Season";
        public const string SeasonNext = "Next Season";
        public const string SeasonPrevious = "Last Season";

        public static readonly List<string> SeasonOptions = [SeasonCurrent, SeasonNext, SeasonPrevious];

        /// <summary>
        /// Returns true if the given list type is the seasonal catalog.
        /// </summary>
        public static bool IsSeasonalListType(ListType list) => list == ListType.Seasonal;

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

        public static Meta ExpiredMeta() 
        {
            return new Meta { id = $"{anilistPrefix}:token-expired", name = "Token expired, re-install addon" };
        }

        public static List<Meta> ExpiredMetas()
        {
            return [ExpiredMeta()];
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

        public static string GetListTypeString(ListType list, TokenData tokenData)
        {
            return (tokenData?.anime_service ?? AnimeService.Kitsu) == AnimeService.Kitsu
                ? list.ToString().ToLower()
                : list.ToString().ToUpper();
        }
    }
}
