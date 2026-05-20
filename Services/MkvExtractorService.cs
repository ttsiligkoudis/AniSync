using AnimeList.Models;
using AnimeList.Services.Interfaces;
using AnimeList.Services.Mkv;
using Microsoft.Extensions.Caching.Memory;
using System.Text;

namespace AnimeList.Services
{
    /// <summary>
    /// In-process MKV subtitle extractor. C# port of the JS
    /// cf-mkv-extractor — same algorithm, same buffer sizes, no
    /// CF Workers per-invocation caps to work around.
    ///
    /// Self-throttles via SemaphoreSlim so a spike of concurrent
    /// extractions can't OOM the Fly machine. Results cache in
    /// IMemoryCache for 2 hours keyed on (url, lang) — same TTL
    /// as the JS worker's edge cache, and equally aligned with
    /// RD-token rotation. Cache hits are O(1) and skip the work
    /// entirely, which is where the 1M-DAU math works out: hot
    /// releases get extracted once and served to everyone watching.
    /// </summary>
    public class MkvExtractorService : IMkvExtractorService
    {
        // Buffer sizes ported from cf-mkv-extractor/src/mkv.js.
        // Keeping the same numbers so behaviour is comparable;
        // tightening can come later once we have Fly-side traces.
        private const int HeadBytes = 256 * 1024;
        private const int TracksFetch = 256 * 1024;
        private const int CuesFetch = 256 * 1024;
        private const int ClusterFetch = 4 * 1024 * 1024;
        private const int ClusterBatchGap = 4 * 1024 * 1024;
        private const int MaxBatchSize = 6 * 1024 * 1024;
        // No CF-style per-invocation cap on Fly. Cap at 500 MB anyway
        // so an oddball file with cues pointing past the end can't
        // wedge us pulling indefinitely.
        private const long MaxTotalFetch = 500L * 1024 * 1024;
        // Inter-batch pacing was a CF/RD-specific workaround; Fly's
        // egress doesn't show the same edge-drop pattern, so skip it.

        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(2);
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

        // Allowed upstream hosts — same allowlist the JS worker
        // enforces. Centralising here keeps a leak-through here
        // from becoming an open-proxy bug.
        private static readonly string[] AllowedHostSuffixes = new[]
        {
            "real-debrid.com",
            "alldebrid.com",
            "debrid-link.com",
            "premiumize.me",
            "torbox.app",
            "offcloud.com",
        };

        private readonly IHttpClientFactory _clientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<MkvExtractorService> _logger;
        private readonly SemaphoreSlim _concurrencyLimit;

        public MkvExtractorService(
            IHttpClientFactory clientFactory,
            IMemoryCache cache,
            IConfiguration configuration,
            ILogger<MkvExtractorService> logger)
        {
            _clientFactory = clientFactory;
            _cache = cache;
            _logger = logger;

            // Max concurrent extractions before new ones queue. Sized
            // for a 1 GB VM: each in-flight extraction peaks at
            // ~50-100 MB of cluster batch buffers. 6 × 100 MB =
            // 600 MB, leaves headroom for the rest of the app.
            // Override via MKV_EXTRACTOR_MAX_CONCURRENT for bigger VMs.
            int maxConcurrent = configuration.GetValue("MKV_EXTRACTOR_MAX_CONCURRENT", 6);
            _concurrencyLimit = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        }

        public static bool IsAllowedHost(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != "https" && u.Scheme != "http") return false;
            var host = u.Host.ToLowerInvariant();
            foreach (var s in AllowedHostSuffixes)
            {
                if (host == s || host.EndsWith("." + s)) return true;
            }
            return false;
        }

        public async Task<MkvExtractResult> ExtractAsync(string url, string? lang, CancellationToken ct)
        {
            var cacheKey = $"mkv-extract:{url}:{lang ?? ""}";
            if (_cache.TryGetValue<MkvExtractResult>(cacheKey, out var cached) && cached is not null)
            {
                return cached;
            }

            await _concurrencyLimit.WaitAsync(ct);
            try
            {
                // Re-check after the wait; another concurrent request
                // for the same URL may have populated the cache while
                // we queued.
                if (_cache.TryGetValue<MkvExtractResult>(cacheKey, out cached) && cached is not null)
                {
                    return cached;
                }

                var result = await ExtractInternalAsync(url, lang, ct);
                if (result.Extracted)
                {
                    _cache.Set(cacheKey, result, CacheTtl);
                }
                return result;
            }
            finally
            {
                _concurrencyLimit.Release();
            }
        }

        private async Task<MkvExtractResult> ExtractInternalAsync(string url, string? lang, CancellationToken ct)
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = FetchTimeout;
            var reader = new RangeReader(url, client);

            try
            {
                await reader.ProbeSizeAsync(ct);
                long totalSize = reader.TotalSize ?? 0;
                _logger.LogInformation("[mkv-extract] start totalSize={Size} lang={Lang}", totalSize, lang ?? "(all)");

                // ── 1. Head ──
                var head = await reader.ReadAsync(0, HeadBytes, ct);

                // ── 2. EBML header + Segment ──
                var ebmlEl = EbmlReader.ReadElement(head, 0);
                if (ebmlEl.Id != EbmlIds.EBML)
                    throw new InvalidDataException("not a Matroska file (no EBML header at byte 0)");
                var segment = EbmlReader.ReadElement(head, ebmlEl.NextOffset);
                if (segment.Id != EbmlIds.Segment)
                    throw new InvalidDataException("expected Segment element after EBML header");
                int segmentDataStart = segment.DataOffset;

                // ── 3. SeekHead ──
                var seekHead = EbmlReader.FindChild(head, segmentDataStart, head.Length, EbmlIds.SeekHead)
                    ?? throw new InvalidDataException("no SeekHead — file lacks an index");
                var seekOffsets = ParseSeekHead(head, seekHead.Value.DataOffset, (int)seekHead.Value.Size);

                // SegmentInfo for TimecodeScale (ns per timecode unit, default 1e6 = ms)
                ulong timecodeScale = 1_000_000;
                if (seekOffsets.TryGetValue(EbmlIds.SegmentInfo, out var infoOffRel))
                {
                    long infoAbs = segmentDataStart + (long)infoOffRel;
                    byte[] infoBuf;
                    int infoLocalOff;
                    if (infoAbs + 4096 <= head.Length)
                    {
                        infoBuf = head;
                        infoLocalOff = (int)infoAbs;
                    }
                    else
                    {
                        infoBuf = await reader.ReadAsync(infoAbs, 4096, ct);
                        infoLocalOff = 0;
                    }
                    try
                    {
                        var infoEl = EbmlReader.ReadElement(infoBuf, infoLocalOff);
                        if (infoEl.Id == EbmlIds.SegmentInfo)
                        {
                            int end = infoEl.DataOffset + (int)infoEl.Size;
                            var ts = EbmlReader.FindChild(infoBuf, infoEl.DataOffset, end, EbmlIds.TimecodeScale);
                            if (ts.HasValue)
                                timecodeScale = EbmlReader.ReadUInt(infoBuf, ts.Value.DataOffset, ts.Value.Size);
                        }
                    }
                    catch { /* keep default */ }
                }

                // ── 4. Tracks ──
                if (!seekOffsets.TryGetValue(EbmlIds.Tracks, out var tracksOffRel))
                    throw new InvalidDataException("SeekHead has no Tracks pointer");
                long tracksAbs = segmentDataStart + (long)tracksOffRel;
                var tracksBuf = await reader.ReadAsync(tracksAbs, TracksFetch, ct);
                var tracksEl = EbmlReader.ReadElement(tracksBuf, 0);
                if (tracksEl.Id != EbmlIds.Tracks)
                    throw new InvalidDataException($"expected Tracks, got id 0x{tracksEl.Id:X}");
                int tracksEnd = tracksEl.DataOffset + (int)tracksEl.Size;
                var allTracks = ParseTracks(tracksBuf, tracksEl.DataOffset, tracksEnd);

                var subtitleTracks = allTracks
                    .Where(t => t.TrackType == EbmlReader.TrackTypeSubtitle && !string.IsNullOrEmpty(t.CodecId))
                    .ToList();
                if (subtitleTracks.Count == 0)
                {
                    return new MkvExtractResult
                    {
                        Tracks = new(),
                        Extracted = true,
                        Stats = new() { FileSize = totalSize, TrackCount = 0 },
                    };
                }

                if (!string.IsNullOrEmpty(lang))
                {
                    subtitleTracks = FilterTracksByLang(subtitleTracks, lang);
                    if (subtitleTracks.Count == 0)
                    {
                        return new MkvExtractResult
                        {
                            Tracks = new(),
                            Extracted = true,
                            Stats = new() { FileSize = totalSize, TrackCount = 0 },
                        };
                    }
                }
                var subTrackNumbers = subtitleTracks.Select(t => t.Number).ToHashSet();

                // ── 5. Cues ──
                if (!seekOffsets.TryGetValue(EbmlIds.Cues, out var cuesOffRel))
                    throw new InvalidDataException("SeekHead has no Cues pointer — file has no index");
                long cuesAbs = segmentDataStart + (long)cuesOffRel;
                var cuesBuf = await reader.ReadAsync(cuesAbs, CuesFetch, ct);
                var cuesEl = EbmlReader.ReadElement(cuesBuf, 0);
                if (cuesEl.Id != EbmlIds.Cues)
                    throw new InvalidDataException($"expected Cues, got id 0x{cuesEl.Id:X}");
                int cuesNeeded = cuesEl.DataOffset + (int)cuesEl.Size;
                if (cuesNeeded > cuesBuf.Length)
                {
                    _logger.LogInformation(
                        "[mkv-extract] cues element is {Size} bytes, initial fetch was {Initial}; topping up",
                        cuesEl.Size, cuesBuf.Length);
                    var extra = await reader.ReadAsync(cuesAbs + cuesBuf.Length, cuesNeeded - cuesBuf.Length, ct);
                    var combined = new byte[cuesNeeded];
                    Array.Copy(cuesBuf, 0, combined, 0, cuesBuf.Length);
                    Array.Copy(extra, 0, combined, cuesBuf.Length, extra.Length);
                    cuesBuf = combined;
                }
                var cuePositions = ParseCues(cuesBuf, cuesEl.DataOffset, cuesNeeded, subTrackNumbers);
                var clusterAbsOffsets = cuePositions
                    .Select(cp => segmentDataStart + (long)cp.ClusterPosition)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();
                _logger.LogInformation("[mkv-extract] clusters={Count}", clusterAbsOffsets.Count);

                // ── 6. Cluster batches ──
                var cuesByTrack = subtitleTracks.ToDictionary(t => t.Number, _ => new List<MkvExtractCue>());
                long totalFetched = HeadBytes + TracksFetch + CuesFetch;

                var batches = new List<(long start, long end)>();
                foreach (var off in clusterAbsOffsets)
                {
                    long newEnd = off + ClusterFetch;
                    if (batches.Count > 0)
                    {
                        var cur = batches[^1];
                        if (off - cur.end < ClusterBatchGap && newEnd - cur.start <= MaxBatchSize)
                        {
                            batches[^1] = (cur.start, newEnd);
                            continue;
                        }
                    }
                    batches.Add((off, newEnd));
                }

                foreach (var batch in batches)
                {
                    int len = (int)(batch.end - batch.start);
                    if (totalFetched + len > MaxTotalFetch)
                    {
                        _logger.LogWarning("[mkv-extract] hit MaxTotalFetch={Cap}; stopping cluster loop", MaxTotalFetch);
                        break;
                    }

                    try { await reader.ReadAsync(batch.start, len, ct); }
                    catch (Exception e)
                    {
                        _logger.LogInformation("[mkv-extract] batch fetch failed, skipping: {Msg}", e.Message);
                        continue;
                    }
                    totalFetched += len;

                    var clustersInBatch = clusterAbsOffsets
                        .Where(o => o >= batch.start && o + ClusterFetch <= batch.end)
                        .ToList();

                    foreach (var clusterAbs in clustersInBatch)
                    {
                        byte[] clusterBuf;
                        try { clusterBuf = await reader.ReadAsync(clusterAbs, ClusterFetch, ct); }
                        catch { continue; }

                        EbmlElement clusterEl;
                        try { clusterEl = EbmlReader.ReadElement(clusterBuf, 0); }
                        catch { continue; }
                        if (clusterEl.Id != EbmlIds.Cluster) continue;

                        int clusterSizeInBuf = clusterEl.Size == ulong.MaxValue
                            ? clusterBuf.Length - clusterEl.DataOffset
                            : Math.Min((int)clusterEl.Size, clusterBuf.Length - clusterEl.DataOffset);
                        int clusterEnd = clusterEl.DataOffset + clusterSizeInBuf;

                        ulong clusterTimecode = 0;
                        var tc = EbmlReader.FindChild(clusterBuf, clusterEl.DataOffset, clusterEnd, EbmlIds.ClusterTimecode);
                        if (tc.HasValue)
                            clusterTimecode = EbmlReader.ReadUInt(clusterBuf, tc.Value.DataOffset, tc.Value.Size);

                        int childOff = clusterEl.DataOffset;
                        while (childOff < clusterEnd)
                        {
                            EbmlElement child;
                            try { child = EbmlReader.ReadElement(clusterBuf, childOff); }
                            catch { break; }
                            if (child.Size == ulong.MaxValue) break;
                            if (child.DataOffset + (int)child.Size > clusterBuf.Length)
                            {
                                childOff = child.DataOffset + (int)child.Size;
                                if (childOff >= clusterEnd) break;
                                continue;
                            }

                            if (child.Id == EbmlIds.SimpleBlock)
                            {
                                // Fast-skip: peek the track-number VINT before
                                // calling parseSimpleBlock. Most blocks are
                                // video/audio that we throw away — skipping
                                // them by track-number-only saves real CPU.
                                ulong tn = PeekTrackNumber(clusterBuf, child.DataOffset);
                                if (subTrackNumbers.Contains(tn))
                                {
                                    var sb = ParseSimpleBlock(clusterBuf, child.DataOffset, (int)child.Size);
                                    if (sb.HasValue)
                                    {
                                        var track = subtitleTracks.First(t => t.Number == sb.Value.TrackNumber);
                                        cuesByTrack[sb.Value.TrackNumber].Add(new MkvExtractCue
                                        {
                                            Time = ((double)clusterTimecode + sb.Value.Timecode) * (timecodeScale / 1e6),
                                            Duration = 4000,
                                            Text = DecodeSubtitle(clusterBuf, sb.Value.PayloadOffset, sb.Value.PayloadSize),
                                        });
                                    }
                                }
                            }
                            else if (child.Id == EbmlIds.BlockGroup)
                            {
                                ParseBlockGroup(
                                    clusterBuf, child.DataOffset, (int)child.Size,
                                    subTrackNumbers, subtitleTracks,
                                    clusterTimecode, timecodeScale,
                                    cuesByTrack);
                            }

                            childOff = child.DataOffset + (int)child.Size;
                        }
                    }

                    reader.Evict(batch.start, batch.end);
                }

                var output = subtitleTracks.Select(t => new MkvExtractTrack
                {
                    Number = (int)t.Number,
                    Language = string.IsNullOrEmpty(t.Language) ? "und" : t.Language,
                    Name = t.Name ?? "",
                    CodecID = t.CodecId,
                    Header = DecodeHeader(t.CodecPrivate, t.CodecId),
                    Cues = cuesByTrack[t.Number],
                }).ToList();

                _logger.LogInformation("[mkv-extract] done tracks={Tracks} cues={Cues}",
                    output.Count, output.Sum(t => t.Cues.Count));

                return new MkvExtractResult
                {
                    Tracks = output,
                    Extracted = true,
                    Stats = new() { FileSize = totalSize, TrackCount = output.Count },
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "[mkv-extract] extraction failed");
                return new MkvExtractResult
                {
                    Tracks = new(),
                    Extracted = false,
                    Reason = ClassifyReason(e.Message),
                    Error = e.Message,
                    Stats = new() { FileSize = reader.TotalSize ?? 0 },
                };
            }
        }

        // ── parsing helpers ──────────────────────────────────────────

        private struct ParsedTrack
        {
            public ulong Number;
            public ulong TrackType;
            public string CodecId;
            public byte[]? CodecPrivate;
            public string Name;
            public string Language;
        }

        private struct ParsedCuePos
        {
            public ulong Track;
            public ulong ClusterPosition;
        }

        private struct ParsedSimpleBlock
        {
            public ulong TrackNumber;
            public int Timecode;
            public int PayloadOffset;
            public int PayloadSize;
        }

        private static Dictionary<ulong, ulong> ParseSeekHead(byte[] buf, int start, int size)
        {
            var result = new Dictionary<ulong, ulong>();
            int off = start;
            int end = Math.Min(start + size, buf.Length);
            while (off < end)
            {
                EbmlElement el;
                try { el = EbmlReader.ReadElement(buf, off); }
                catch { break; }

                if (el.Id == EbmlIds.Seek)
                {
                    int seekEnd = Math.Min(el.DataOffset + (int)el.Size, buf.Length);
                    int subOff = el.DataOffset;
                    ulong? idVal = null;
                    ulong? posVal = null;
                    while (subOff < seekEnd)
                    {
                        EbmlElement sub;
                        try { sub = EbmlReader.ReadElement(buf, subOff); }
                        catch { break; }

                        if (sub.Id == EbmlIds.SeekId)
                        {
                            try
                            {
                                var (v, _) = EbmlReader.ReadVint(buf, sub.DataOffset, true);
                                idVal = v;
                            }
                            catch { }
                        }
                        else if (sub.Id == EbmlIds.SeekPosition)
                        {
                            posVal = EbmlReader.ReadUInt(buf, sub.DataOffset, sub.Size);
                        }
                        subOff = sub.NextOffset;
                    }
                    if (idVal.HasValue && posVal.HasValue) result[idVal.Value] = posVal.Value;
                }

                off = el.NextOffset;
            }
            return result;
        }

        private static List<ParsedTrack> ParseTracks(byte[] buf, int start, int end)
        {
            var result = new List<ParsedTrack>();
            int off = start;
            int limit = Math.Min(end, buf.Length);
            while (off < limit)
            {
                EbmlElement el;
                try { el = EbmlReader.ReadElement(buf, off); }
                catch { break; }

                if (el.Id == EbmlIds.TrackEntry)
                {
                    int entryEnd = Math.Min(el.DataOffset + (int)el.Size, buf.Length);
                    result.Add(ParseTrackEntry(buf, el.DataOffset, entryEnd));
                }
                off = el.NextOffset;
            }
            return result;
        }

        private static ParsedTrack ParseTrackEntry(byte[] buf, int start, int end)
        {
            var t = new ParsedTrack { CodecId = "", Name = "", Language = "" };
            int off = start;
            while (off < end && off < buf.Length)
            {
                EbmlElement el;
                try { el = EbmlReader.ReadElement(buf, off); }
                catch { break; }
                if (el.DataOffset + (int)el.Size > buf.Length) break;

                switch (el.Id)
                {
                    case EbmlIds.TrackNumber: t.Number = EbmlReader.ReadUInt(buf, el.DataOffset, el.Size); break;
                    case EbmlIds.TrackType: t.TrackType = EbmlReader.ReadUInt(buf, el.DataOffset, el.Size); break;
                    case EbmlIds.CodecId: t.CodecId = EbmlReader.ReadString(buf, el.DataOffset, el.Size); break;
                    case EbmlIds.CodecPrivate: t.CodecPrivate = EbmlReader.ReadBytes(buf, el.DataOffset, el.Size); break;
                    case EbmlIds.Name: t.Name = EbmlReader.ReadString(buf, el.DataOffset, el.Size); break;
                    case EbmlIds.Language: t.Language = EbmlReader.ReadString(buf, el.DataOffset, el.Size); break;
                }
                off = el.NextOffset;
            }
            return t;
        }

        private static List<ParsedCuePos> ParseCues(byte[] buf, int start, int end, HashSet<ulong> subTrackSet)
        {
            var result = new List<ParsedCuePos>();
            int off = start;
            int limit = Math.Min(end, buf.Length);
            while (off < limit)
            {
                EbmlElement cp;
                try { cp = EbmlReader.ReadElement(buf, off); }
                catch { break; }

                if (cp.Id == EbmlIds.CuePoint)
                {
                    int cpEnd = Math.Min(cp.DataOffset + (int)cp.Size, buf.Length);
                    int pos = cp.DataOffset;
                    var positions = new List<ParsedCuePos>();
                    while (pos < cpEnd)
                    {
                        EbmlElement inner;
                        try { inner = EbmlReader.ReadElement(buf, pos); }
                        catch { break; }

                        if (inner.Id == EbmlIds.CueTrackPositions)
                        {
                            var tp = ParseCueTrackPositions(buf, inner.DataOffset, inner.DataOffset + (int)inner.Size);
                            if (tp.HasValue && subTrackSet.Contains(tp.Value.Track))
                                positions.Add(tp.Value);
                        }
                        // CueTime intentionally ignored — we don't use it
                        pos = inner.NextOffset;
                    }
                    result.AddRange(positions);
                }

                if (cp.NextOffset > buf.Length) break;
                off = cp.NextOffset;
            }
            return result;
        }

        private static ParsedCuePos? ParseCueTrackPositions(byte[] buf, int start, int end)
        {
            ulong track = 0;
            ulong? clusterPosition = null;
            int off = start;
            int limit = Math.Min(end, buf.Length);
            while (off < limit)
            {
                EbmlElement el;
                try { el = EbmlReader.ReadElement(buf, off); }
                catch { break; }

                switch (el.Id)
                {
                    case EbmlIds.CueTrack: track = EbmlReader.ReadUInt(buf, el.DataOffset, el.Size); break;
                    case EbmlIds.CueClusterPosition: clusterPosition = EbmlReader.ReadUInt(buf, el.DataOffset, el.Size); break;
                }
                off = el.NextOffset;
            }
            return clusterPosition.HasValue ? new ParsedCuePos { Track = track, ClusterPosition = clusterPosition.Value } : null;
        }

        private static ulong PeekTrackNumber(byte[] buf, int off)
        {
            // 1-byte VINT covers track numbers 1-127 (every real-world file).
            if (off >= buf.Length) return 0;
            byte fb = buf[off];
            if ((fb & 0x80) != 0) return (ulong)(byte)(fb & 0x7F);
            // Multi-byte fallback for the rare > 127 case.
            try { return EbmlReader.ReadVint(buf, off, false).value; }
            catch { return 0; }
        }

        private static ParsedSimpleBlock? ParseSimpleBlock(byte[] buf, int off, int size)
        {
            if (size < 4) return null;
            int p = off;
            ulong tn;
            int tnLen;
            try { (tn, tnLen) = EbmlReader.ReadVint(buf, p, false); }
            catch { return null; }
            p += tnLen;
            int timecode = EbmlReader.ReadI16Be(buf, p);
            p += 2;
            // flags byte
            p += 1;
            int payloadEnd = off + size;
            if (p > payloadEnd) return null;
            return new ParsedSimpleBlock
            {
                TrackNumber = tn,
                Timecode = timecode,
                PayloadOffset = p,
                PayloadSize = payloadEnd - p,
            };
        }

        private static void ParseBlockGroup(
            byte[] buf, int start, int size,
            HashSet<ulong> subTrackNumbers, List<ParsedTrack> subtitleTracks,
            ulong clusterTimecode, ulong timecodeScale,
            Dictionary<ulong, List<MkvExtractCue>> cuesByTrack)
        {
            int end = Math.Min(start + size, buf.Length);
            ParsedSimpleBlock? block = null;
            ulong? durationRaw = null;
            int off = start;
            while (off < end)
            {
                EbmlElement el;
                try { el = EbmlReader.ReadElement(buf, off); }
                catch { break; }

                if (el.Id == EbmlIds.Block)
                {
                    block = ParseSimpleBlock(buf, el.DataOffset, (int)el.Size);
                }
                else if (el.Id == EbmlIds.BlockDuration)
                {
                    durationRaw = EbmlReader.ReadUInt(buf, el.DataOffset, el.Size);
                }
                off = el.NextOffset;
            }

            if (block.HasValue && subTrackNumbers.Contains(block.Value.TrackNumber))
            {
                double durationMs = durationRaw.HasValue
                    ? durationRaw.Value * (timecodeScale / 1e6)
                    : 4000;
                cuesByTrack[block.Value.TrackNumber].Add(new MkvExtractCue
                {
                    Time = ((double)clusterTimecode + block.Value.Timecode) * (timecodeScale / 1e6),
                    Duration = durationMs,
                    Text = DecodeSubtitle(buf, block.Value.PayloadOffset, block.Value.PayloadSize),
                });
            }
        }

        private static string DecodeSubtitle(byte[] buf, int off, int size)
        {
            int end = Math.Min(off + size, buf.Length);
            return Encoding.UTF8.GetString(buf, off, end - off);
        }

        private static string DecodeHeader(byte[]? codecPrivate, string codecId)
        {
            if (codecPrivate is null) return "";
            if (codecId == "S_TEXT/ASS" || codecId == "S_TEXT/SSA")
            {
                return Encoding.UTF8.GetString(codecPrivate);
            }
            return "";
        }

        private static List<ParsedTrack> FilterTracksByLang(List<ParsedTrack> tracks, string lang)
        {
            static bool IsEnglish(ParsedTrack t)
            {
                var l = (t.Language ?? "").ToLowerInvariant();
                var n = (t.Name ?? "").ToLowerInvariant();
                return l.StartsWith("en") || n.Contains("english") || n.Contains("eng");
            }

            var lc = lang.ToLowerInvariant();
            if (lc == "auto")
            {
                var en = tracks.FirstOrDefault(IsEnglish);
                return en.CodecId is not null ? new() { en } : new();
            }
            var shortCode = lc.Length >= 2 ? lc.Substring(0, 2) : lc;
            return tracks
                .Where(t =>
                {
                    var l = (t.Language ?? "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(l)) return false;
                    return l == lc || l.StartsWith(shortCode);
                })
                .ToList();
        }

        private static string ClassifyReason(string msg)
        {
            if (msg.Contains("no SeekHead") || msg.Contains("no Tracks pointer") || msg.Contains("no index"))
                return "indexless";
            if (msg.Contains("upstream HTTP") || msg.Contains("Range ignored") || msg.Contains("timed out"))
                return "network";
            return "parse";
        }
    }
}
