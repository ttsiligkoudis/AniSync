using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace AnimeList.Services.Mkv
{
    /// <summary>
    /// Range-aware reader over an HTTP URL. Port of
    /// cf-mkv-extractor/src/reader.js, minus the cap-driven divide-
    /// and-conquer retry strategies — those were workarounds for
    /// CF's per-invocation memory cap and RD's specific behaviour
    /// against CF worker IPs. Fly.io's IPs don't show the same
    /// drop pattern in practice, so we ship the simpler reader.
    /// If we observe deep-offset drops here too, the larger-bounded
    /// + open-ended fallback can be ported back in.
    /// </summary>
    internal sealed class RangeReader
    {
        private const long CacheCap = 16 * 1024 * 1024;
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0 Safari/537.36";

        private readonly string _url;
        private readonly HttpClient _client;
        private readonly List<CachedChunk> _chunks = new();

        public long? TotalSize { get; private set; }

        public RangeReader(string url, HttpClient client)
        {
            _url = url;
            _client = client;
        }

        private sealed class CachedChunk
        {
            public long Start;
            public byte[] Bytes = Array.Empty<byte>();
        }

        /// <summary>
        /// Returns the bytes for [start, start+length). Cache hits
        /// when any cached chunk wholly contains the range; otherwise
        /// fires a Range request and caches the result.
        /// </summary>
        public async Task<byte[]> ReadAsync(long start, int length, CancellationToken ct)
        {
            if (length == 0) return Array.Empty<byte>();

            foreach (var c in _chunks)
            {
                long end = c.Start + c.Bytes.Length;
                if (c.Start <= start && end >= start + length)
                {
                    int offset = (int)(start - c.Start);
                    var copy = new byte[length];
                    Array.Copy(c.Bytes, offset, copy, 0, length);
                    return copy;
                }
            }

            var bytes = await FetchAsync(start, length, ct);
            _chunks.Add(new CachedChunk { Start = start, Bytes = bytes });
            EvictCache();
            return bytes;
        }

        /// <summary>Ensures TotalSize is known. Reads 1 byte at offset 0 if not.</summary>
        public async Task<long> ProbeSizeAsync(CancellationToken ct)
        {
            if (TotalSize.HasValue) return TotalSize.Value;
            await ReadAsync(0, 1, ct);
            return TotalSize ?? 0;
        }

        /// <summary>Drops cached chunks whose start falls inside [start, end).</summary>
        public void Evict(long start, long end)
        {
            _chunks.RemoveAll(c => c.Start >= start && c.Start < end);
        }

        private async Task<byte[]> FetchAsync(long start, int length, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _url);
            req.Headers.Range = new RangeHeaderValue(start, start + length - 1);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.Accept.Clear();
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            // Try HTTP/2 with HTTP/1.1 fallback. RD's CDN supports
            // HTTP/2; multiplexing many Range requests on one
            // connection halves per-request overhead vs the
            // HttpClient default (HTTP/1.1 keep-alive).
            req.Version = HttpVersion.Version20;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode != HttpStatusCode.PartialContent && res.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"upstream HTTP {(int)res.StatusCode} on range {start}-{start + length - 1}");
            }

            if (!TotalSize.HasValue)
            {
                if (res.Content.Headers.ContentRange?.Length is long ttl)
                {
                    TotalSize = ttl;
                }
                else if (res.StatusCode == HttpStatusCode.OK && res.Content.Headers.ContentLength is long cl)
                {
                    TotalSize = cl;
                }
            }

            // 200 OK with start > 0 means Range was ignored. Bail before
            // we drain a multi-GB body and blow memory.
            if (res.StatusCode == HttpStatusCode.OK && start > 0)
            {
                throw new InvalidOperationException(
                    $"upstream returned 200 (Range ignored) for {start}-{start + length - 1}; " +
                    "cannot fetch arbitrary offset");
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var buf = new byte[length];
            int written = 0;
            try
            {
                while (written < length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(written, length - written), ct);
                    if (n == 0) break;
                    written += n;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // honour the caller's cancellation token
            }
            catch (IOException)
            {
                // RD's edges sometimes close the connection before
                // delivering all bytes promised by Content-Length —
                // surfaces as "response ended prematurely" / HttpIOException.
                // Treat as EOF and return whatever we got; the EBML
                // parser's element walkers already handle short buffers
                // by bailing when an element extends past the slab.
                // For critical reads (head/tracks/cues) where we need
                // a specific element, a too-short buffer will surface
                // as "no SeekHead" / "expected Tracks" upstream which
                // classifies as parse / indexless cleanly.
            }
            if (written < length)
            {
                Array.Resize(ref buf, written);
            }
            return buf;
        }

        private void EvictCache()
        {
            long total = 0;
            foreach (var c in _chunks) total += c.Bytes.Length;
            while (total > CacheCap && _chunks.Count > 2)
            {
                total -= _chunks[0].Bytes.Length;
                _chunks.RemoveAt(0);
            }
        }
    }
}
