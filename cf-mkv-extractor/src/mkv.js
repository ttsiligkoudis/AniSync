// Indexed Matroska subtitle extractor.
//
// Walks the EBML/Matroska structure of the file fetched through
// RangeReader, finds the SeekHead → Tracks → Cues chain, identifies
// subtitle TrackEntries, and pulls only the Clusters that Cues says
// contain blocks for those tracks. For a typical 700 MB anime episode
// this lands at ~10-30 MB of total network usage instead of the full
// file size.
//
// Output shape mirrors what the in-app matroska-subtitles consumer
// already expects so the client can drop this in with minimal changes:
//   {
//     tracks: [
//       { number, language, name, codecID, header, cues: [
//           { time, duration, text },
//           …
//       ] },
//       …
//     ]
//   }

import {
    readVint, readElement, readUInt, readSInt, readString, readBytes, ID,
    TRACK_TYPE_SUBTITLE,
} from './ebml.js';

// With lang=auto in the default flow the per-Worker memory pressure
// drops significantly (cuesByTrack carries one track instead of all
// eleven), so we can spend the freed budget on larger Range requests.
// Bigger fetches = fewer connections to open = fewer chances for
// RD's edges to drop us mid-stream with "Network connection lost".
// Still under the 128 MB Worker memory cap even with the in-flight
// stream buffer + cache + framework overhead — peak math is in the
// commit history and works out to ~80 MB worst case.
const HEAD_BYTES = 256 * 1024;
const TRACKS_FETCH = 256 * 1024;
const CUES_FETCH = 4 * 1024 * 1024;
const CLUSTER_FETCH = 6 * 1024 * 1024;
const CLUSTER_BATCH_GAP = 6 * 1024 * 1024;
// Per-batch span. Sized to maximise the bytes-per-connection ratio
// (each fetch carries more sub data → fewer total connections →
// fewer mid-stream-drop opportunities) while staying clear of the
// 128 MB Worker memory cap when combined with the cache, framework
// overhead, and in-flight Response body.
const MAX_BATCH_SIZE = 24 * 1024 * 1024;
// Cloudflare Workers Free caps us at 50 subrequests per invocation.
// Bigger batches → far fewer batches needed → MAX_CLUSTER_BATCHES
// is rarely the binding constraint, but kept conservative so retries
// against transient failures stay within the cap.
const MAX_CLUSTER_BATCHES = 20;
// Total bytes ceiling. Bumped to absorb the bigger per-batch size
// while still leaving enough headroom under the 128 MB Worker cap
// after cache + framework + in-flight buffer.
const MAX_TOTAL_FETCH = 120 * 1024 * 1024;
// Delay inserted between consecutive cluster-batch fetches. Small
// enough to be invisible from the user's perspective (~2s of extra
// wall-clock on a typical extraction) but enough to break up the
// burst pattern that triggers RD's edge load balancers into dropping
// mid-stream. Doesn't count against the Worker CPU budget — `await
// new Promise(setTimeout)` is a yield point.
const BATCH_PACING_MS = 100;

export async function extractSubtitles(reader, options) {
    const opts = options || {};
    await reader.probeSize();

    // ── 1. Read the file head ─────────────────────────────────────
    const head = await reader.readCritical(0, HEAD_BYTES);

    // ── 2. Skip EBML header, locate Segment ───────────────────────
    let off = 0;
    const ebml = readElement(head, off);
    if (ebml.id !== ID.EBML) {
        throw new Error('not a Matroska file (no EBML header at byte 0)');
    }
    off = ebml.nextOffset;
    const segment = readElement(head, off);
    if (segment.id !== ID.Segment) {
        throw new Error('expected Segment element after EBML header');
    }
    const segmentDataStart = segment.dataOffset;
    // SeekHead offsets are relative to segmentDataStart.
    const absoluteFromSegment = (relative) => segmentDataStart + relative;

    // ── 3. Inside Segment, locate SeekHead ────────────────────────
    // The first child of Segment is almost always SeekHead (well-muxed
    // files put it there to make exactly this kind of walk efficient).
    const seekHead = findChild(head, segmentDataStart, HEAD_BYTES, ID.SeekHead);
    if (!seekHead) {
        // No SeekHead — would need a full-file scan. Not worth the
        // bandwidth for our use case. Caller falls back to streaming.
        throw new Error('no SeekHead — file lacks an index');
    }
    const seekOffsets = parseSeekHead(head, seekHead.dataOffset, seekHead.size);

    // SegmentInfo gives us TimecodeScale (ns per timecode unit, default 1e6 = ms).
    let timecodeScale = 1_000_000;
    const infoOff = seekOffsets[ID.SegmentInfo];
    if (infoOff !== undefined) {
        const infoAbs = absoluteFromSegment(infoOff);
        // Try to use head if Info fits there, else fetch a small slab.
        const infoBuf = infoAbs + 4096 <= HEAD_BYTES
            ? head
            : await reader.read(infoAbs, 4096);
        const infoLocalOff = infoAbs + 4096 <= HEAD_BYTES ? infoAbs : 0;
        try {
            const infoEl = readElement(infoBuf, infoLocalOff);
            if (infoEl.id === ID.SegmentInfo) {
                const ts = findChild(infoBuf, infoEl.dataOffset, infoEl.dataOffset + infoEl.size, ID.TimecodeScale);
                if (ts) timecodeScale = readUInt(infoBuf, ts.dataOffset, ts.size);
            }
        } catch (_) { /* keep default */ }
    }

    // ── 4. Read Tracks, identify subtitle TrackEntries ────────────
    const tracksOff = seekOffsets[ID.Tracks];
    if (tracksOff === undefined) {
        throw new Error('SeekHead has no Tracks pointer');
    }
    const tracksAbs = absoluteFromSegment(tracksOff);
    const tracksBuf = await reader.readCritical(tracksAbs, TRACKS_FETCH);
    const tracksEl = readElement(tracksBuf, 0);
    if (tracksEl.id !== ID.Tracks) {
        throw new Error(`expected Tracks at ${tracksAbs}, got id 0x${tracksEl.id.toString(16)}`);
    }
    const tracks = parseTracks(tracksBuf, tracksEl.dataOffset, tracksEl.dataOffset + tracksEl.size);
    let subtitleTracks = tracks.filter(t => t.type === TRACK_TYPE_SUBTITLE && t.codecID);
    if (subtitleTracks.length === 0) {
        return { tracks: [] };
    }
    // Optional language filter. Cuts the per-cluster parse cost and
    // the JSON response size by however many tracks we drop. The
    // bandwidth saving is smaller because Clusters carry every
    // track's data interleaved, but in files where Cues are indexed
    // per-track this can also reduce the unique Cluster offsets we
    // have to fetch.
    if (opts.lang) {
        subtitleTracks = filterTracksByLang(subtitleTracks, opts.lang);
        if (subtitleTracks.length === 0) {
            return { tracks: [] };
        }
    }
    const subTrackNumbers = new Set(subtitleTracks.map(t => t.number));

    // ── 5. Read Cues, collect subtitle Cluster positions ──────────
    const cuesOff = seekOffsets[ID.Cues];
    if (cuesOff === undefined) {
        throw new Error('SeekHead has no Cues pointer — file has no index');
    }
    const cuesAbs = absoluteFromSegment(cuesOff);
    const cuesBuf = await reader.readCritical(cuesAbs, CUES_FETCH);
    const cuesEl = readElement(cuesBuf, 0);
    if (cuesEl.id !== ID.Cues) {
        throw new Error(`expected Cues at ${cuesAbs}, got id 0x${cuesEl.id.toString(16)}`);
    }
    const cuePositions = parseCues(cuesBuf, cuesEl.dataOffset, cuesEl.dataOffset + cuesEl.size, subTrackNumbers);
    // Unique cluster offsets — same cluster often has cues for multiple tracks.
    const clusterAbsOffsets = Array.from(new Set(
        cuePositions.map(cp => absoluteFromSegment(cp.clusterPosition))
    )).sort((a, b) => a - b);

    // Sharding: when the caller specifies shards>1, slice the offset
    // list into contiguous ranges and only process this shard's slice.
    // Each shard runs as its own Worker invocation with its own
    // 128 MB memory cap + 50-subrequest budget, so 4 shards lets a
    // big BD remux extract ~480 MB worth of cluster bytes total
    // instead of the 120 MB ceiling a single invocation has.
    //
    // Contiguous slices (not round-robin) so each shard's clusters
    // are near each other in the file — the batch coalescer below
    // can then merge them into a few fat Range requests instead of
    // many small scattered ones, which matters for the 50-subrequest
    // budget. The browser merges shard responses by track number
    // and sorts cues by time so any slicing scheme produces the
    // same final track, but contiguous is by far the cheapest.
    const shards = Math.max(1, Math.floor(opts.shards || 1));
    const shardIdx = Math.max(0, Math.min(shards - 1, Math.floor(opts.shard || 0)));
    let activeOffsets = clusterAbsOffsets;
    if (shards > 1) {
        const sliceSize = Math.ceil(clusterAbsOffsets.length / shards);
        const sliceStart = shardIdx * sliceSize;
        const sliceEnd = Math.min(sliceStart + sliceSize, clusterAbsOffsets.length);
        activeOffsets = clusterAbsOffsets.slice(sliceStart, sliceEnd);
    }

    // ── 6. For each Cluster, pull subtitle blocks ─────────────────
    const cuesByTrack = new Map(subtitleTracks.map(t => [t.number, []]));
    let totalFetched = HEAD_BYTES + TRACKS_FETCH + CUES_FETCH;
    let truncated = false;

    // Coalesce nearby cluster offsets into Range batches so a long
    // episode with many cue points doesn't blow Cloudflare Workers'
    // 50-subrequest-per-invocation cap. Two-pass:
    //   (a) Greedy: adjacent offsets within CLUSTER_BATCH_GAP share
    //       one batch — capped at MAX_BATCH_SIZE so no single fetch
    //       blows the 128 MB Worker memory cap.
    //   (b) Coarsen: if greedy still produced more than
    //       MAX_CLUSTER_BATCHES batches, repeatedly merge the
    //       smallest-gap neighbour pair until we fit the budget,
    //       refusing any merge that would push the resulting batch
    //       past MAX_BATCH_SIZE.
    // We then fetch + process + evict each batch sequentially so the
    // resident set stays small even on long extractions.
    const batches = [];
    for (const off of activeOffsets) {
        const cur = batches[batches.length - 1];
        if (cur
            && off - cur.end < CLUSTER_BATCH_GAP
            && (off + CLUSTER_FETCH) - cur.start <= MAX_BATCH_SIZE) {
            cur.end = off + CLUSTER_FETCH;
        } else {
            batches.push({ start: off, end: off + CLUSTER_FETCH });
        }
    }
    while (batches.length > MAX_CLUSTER_BATCHES) {
        let mergeIdx = -1;
        let minGap = Infinity;
        for (let i = 0; i < batches.length - 1; i++) {
            const mergedSize = batches[i + 1].end - batches[i].start;
            if (mergedSize > MAX_BATCH_SIZE) continue;
            const gap = batches[i + 1].start - batches[i].end;
            if (gap < minGap) { minGap = gap; mergeIdx = i; }
        }
        if (mergeIdx < 0) break; // every adjacent merge would overflow the size cap
        batches[mergeIdx].end = batches[mergeIdx + 1].end;
        batches.splice(mergeIdx + 1, 1);
    }

    // Group cluster offsets by which batch contains them so each
    // batch can be processed in isolation and evicted right after.
    const clustersInBatch = batches.map(b =>
        activeOffsets.filter(off => off >= b.start && off + CLUSTER_FETCH <= b.end)
    );

    for (let bi = 0; bi < batches.length; bi++) {
        const b = batches[bi];
        const len = b.end - b.start;
        if (totalFetched + len > MAX_TOTAL_FETCH) {
            // Surfaced to the caller so the client can re-fire with
            // sharding (or escalate from N=4 to N=8 etc.). Without
            // this flag the truncation was silent — caller saw a
            // full-looking response with quietly missing cues.
            truncated = true;
            break;
        }
        try {
            await reader.read(b.start, len);
            totalFetched += len;
        } catch (_) { continue; /* batch failed — its clusters are lost */ }

        for (const clusterAbs of clustersInBatch[bi]) {
            let clusterBuf;
            try { clusterBuf = await reader.read(clusterAbs, CLUSTER_FETCH); }
            catch (_) { continue; }
        let clusterEl;
        try { clusterEl = readElement(clusterBuf, 0); }
        catch (e) { continue; }
        if (clusterEl.id !== ID.Cluster) continue;

        // Find the cluster's base timecode first.
        const tc = findChild(clusterBuf, clusterEl.dataOffset, clusterEl.dataOffset + Math.min(clusterEl.size, clusterBuf.length), ID.ClusterTimecode);
        const clusterTimecode = tc ? readUInt(clusterBuf, tc.dataOffset, tc.size) : 0;

        // Walk every direct child of the Cluster, picking out
        // SimpleBlock / BlockGroup elements whose track number is one
        // of our subtitle tracks.
        const clusterEnd = Math.min(clusterEl.size, clusterBuf.length - clusterEl.dataOffset) + clusterEl.dataOffset;
        let childOff = clusterEl.dataOffset;
        while (childOff < clusterEnd) {
            let child;
            try { child = readElement(clusterBuf, childOff); }
            catch (_) { break; }
            // Big video SimpleBlocks routinely exceed what we have in
            // our slab. The element HEADER tells us how far past the
            // buffer the body extends — use that to advance childOff
            // by the full element size so we keep alignment for blocks
            // that come AFTER this one. (Without the skip-by-size we'd
            // lose track of where the next element starts.)
            // Unknown-size elements (rare on writes) we can't skip
            // past — give up on the rest of this cluster.
            if (child.size === Infinity) break;
            if (child.dataOffset + child.size > clusterBuf.length) {
                childOff = child.dataOffset + child.size;
                if (childOff >= clusterEnd) break;
                continue;
            }

            if (child.id === ID.SimpleBlock) {
                const sb = parseSimpleBlock(clusterBuf, child.dataOffset, child.size);
                if (sb && subTrackNumbers.has(sb.trackNumber)) {
                    const track = subtitleTracks.find(t => t.number === sb.trackNumber);
                    cuesByTrack.get(sb.trackNumber).push({
                        time: (clusterTimecode + sb.timecode) * (timecodeScale / 1e6),
                        // SimpleBlock has no duration field; fall back
                        // to a sensible default (most ASS dialogue is
                        // 2-4s). Players ignore overlapping cue ends.
                        duration: 4000,
                        text: decodeSubtitle(sb.payload, track && track.codecID),
                    });
                }
            } else if (child.id === ID.BlockGroup) {
                const bg = parseBlockGroup(clusterBuf, child.dataOffset, child.size);
                if (bg && bg.block && subTrackNumbers.has(bg.block.trackNumber)) {
                    const track = subtitleTracks.find(t => t.number === bg.block.trackNumber);
                    cuesByTrack.get(bg.block.trackNumber).push({
                        time: (clusterTimecode + bg.block.timecode) * (timecodeScale / 1e6),
                        duration: (bg.duration ?? 4000) * (timecodeScale / 1e6),
                        text: decodeSubtitle(bg.block.payload, track && track.codecID),
                    });
                }
            }

            childOff = child.dataOffset + child.size;
        }
        }

        // Evict this batch's bytes before fetching the next one —
        // keeps resident memory bounded at roughly one batch + the
        // small head / tracks / cues slabs regardless of how many
        // batches we process.
        reader.evict(b.start, b.end);

        // Pace successive batches. Back-to-back Range requests from
        // a single Worker IP appear to make RD's edge load balancers
        // grumpy — they intermittently drop connections mid-stream
        // with "Network connection lost". A small inter-batch delay
        // spreads the traffic enough to avoid the burst pattern
        // without meaningfully slowing extraction (≤ 2s extra on a
        // ~20-batch episode). await on a Promise-timeout is a yield
        // point, not CPU time — doesn't count against the Worker
        // CPU budget. Skip the delay after the last batch.
        if (bi < batches.length - 1) {
            await new Promise(resolve => setTimeout(resolve, BATCH_PACING_MS));
        }
    }

    // ── 7. Stitch it together ─────────────────────────────────────
    return {
        tracks: subtitleTracks.map(t => ({
            number: t.number,
            language: t.language || 'und',
            name: t.name || '',
            codecID: t.codecID,
            header: decodeHeader(t.codecPrivate, t.codecID),
            cues: cuesByTrack.get(t.number) || [],
        })),
        truncated,
        // Echoed so the caller can confirm which slice this response
        // covers — useful when debugging a failed merge.
        shard: shardIdx,
        shards,
        // Total cluster offsets in the full file. Lets the client
        // estimate how many shards are needed when sharding kicks in
        // (rough rule of thumb: clusters-per-shard × per-cluster-size
        // should fit under MAX_TOTAL_FETCH).
        clustersTotal: clusterAbsOffsets.length,
        clustersInShard: activeOffsets.length,
    };
}

/**
 * Walks a contiguous EBML buffer scanning for a direct-child element
 * with the requested ID. Returns the element header, or null if not
 * found within the bounds.
 */
function findChild(buf, start, end, targetId) {
    let off = start;
    while (off < end && off < buf.length) {
        let el;
        try { el = readElement(buf, off); }
        catch (_) { return null; }
        if (el.id === targetId) return el;
        if (el.size === Infinity || el.nextOffset > buf.length) return null;
        off = el.nextOffset;
    }
    return null;
}

/** Parses SeekHead → { [elementId]: byteOffsetInsideSegment, … } */
function parseSeekHead(buf, start, size) {
    const out = {};
    const end = start + size;
    let off = start;
    while (off < end) {
        let seek;
        try { seek = readElement(buf, off); }
        catch (_) { break; }
        if (seek.id === ID.Seek) {
            const idEl = findChild(buf, seek.dataOffset, seek.dataOffset + seek.size, ID.SeekID);
            const posEl = findChild(buf, seek.dataOffset, seek.dataOffset + seek.size, ID.SeekPosition);
            if (idEl && posEl) {
                const targetId = readUInt(buf, idEl.dataOffset, idEl.size);
                const targetPos = readUInt(buf, posEl.dataOffset, posEl.size);
                out[targetId] = targetPos;
            }
        }
        off = seek.nextOffset;
    }
    return out;
}

function parseTracks(buf, start, end) {
    const list = [];
    let off = start;
    while (off < end && off < buf.length) {
        let entry;
        try { entry = readElement(buf, off); }
        catch (_) { break; }
        if (entry.id === ID.TrackEntry) {
            list.push(parseTrackEntry(buf, entry.dataOffset, entry.dataOffset + entry.size));
        }
        if (entry.nextOffset > buf.length) break;
        off = entry.nextOffset;
    }
    return list;
}

function parseTrackEntry(buf, start, end) {
    const t = {
        number: 0, type: 0, codecID: '',
        codecPrivate: null, name: '', language: '',
    };
    let off = start;
    while (off < end && off < buf.length) {
        let el;
        try { el = readElement(buf, off); }
        catch (_) { break; }
        if (el.dataOffset + el.size > buf.length) break;
        switch (el.id) {
            case ID.TrackNumber: t.number = readUInt(buf, el.dataOffset, el.size); break;
            case ID.TrackType:   t.type = readUInt(buf, el.dataOffset, el.size); break;
            case ID.CodecID:     t.codecID = readString(buf, el.dataOffset, el.size); break;
            case ID.CodecPrivate: t.codecPrivate = readBytes(buf, el.dataOffset, el.size); break;
            case ID.Name:        t.name = readString(buf, el.dataOffset, el.size); break;
            case ID.Language:    t.language = readString(buf, el.dataOffset, el.size); break;
        }
        off = el.nextOffset;
    }
    return t;
}

function parseCues(buf, start, end, subTrackSet) {
    const out = [];
    let off = start;
    while (off < end && off < buf.length) {
        let cp;
        try { cp = readElement(buf, off); }
        catch (_) { break; }
        if (cp.id === ID.CuePoint) {
            const cpEnd = cp.dataOffset + cp.size;
            let pos = cp.dataOffset;
            let cueTime = 0;
            const trackPositions = [];
            while (pos < cpEnd) {
                let inner;
                try { inner = readElement(buf, pos); }
                catch (_) { break; }
                if (inner.id === ID.CueTime) {
                    cueTime = readUInt(buf, inner.dataOffset, inner.size);
                } else if (inner.id === ID.CueTrackPositions) {
                    const tp = parseCueTrackPositions(buf, inner.dataOffset, inner.dataOffset + inner.size);
                    if (tp && subTrackSet.has(tp.track)) trackPositions.push(tp);
                }
                pos = inner.nextOffset;
            }
            for (const tp of trackPositions) {
                out.push({ time: cueTime, track: tp.track, clusterPosition: tp.clusterPosition });
            }
        }
        if (cp.nextOffset > buf.length) break;
        off = cp.nextOffset;
    }
    return out;
}

function parseCueTrackPositions(buf, start, end) {
    let track = 0, clusterPosition = -1;
    let off = start;
    while (off < end && off < buf.length) {
        let el;
        try { el = readElement(buf, off); }
        catch (_) { break; }
        if (el.id === ID.CueTrack) track = readUInt(buf, el.dataOffset, el.size);
        else if (el.id === ID.CueClusterPosition) clusterPosition = readUInt(buf, el.dataOffset, el.size);
        off = el.nextOffset;
    }
    if (clusterPosition < 0) return null;
    return { track, clusterPosition };
}

/**
 * Parses a Block / SimpleBlock body (the bytes inside the element).
 * Format:  TrackNumber (vint) | Timecode (sint16) | Flags (u8) | Frame data
 */
function parseSimpleBlock(buf, off, size) {
    if (size < 4) return null;
    let p = off;
    const tn = readVint(buf, p, false);
    p += tn.length;
    const timecode = readSInt(buf, p, 2);
    p += 2;
    /* const flags = buf[p]; */
    p += 1;
    const payloadEnd = off + size;
    return {
        trackNumber: tn.value,
        timecode,
        payload: readBytes(buf, p, payloadEnd - p),
    };
}

function parseBlockGroup(buf, start, size) {
    const end = start + size;
    let block = null;
    let duration = null;
    let off = start;
    while (off < end && off < buf.length) {
        let el;
        try { el = readElement(buf, off); }
        catch (_) { break; }
        if (el.id === ID.Block) {
            block = parseSimpleBlock(buf, el.dataOffset, el.size);
        } else if (el.id === ID.BlockDuration) {
            duration = readUInt(buf, el.dataOffset, el.size);
        }
        off = el.nextOffset;
    }
    return { block, duration };
}

/**
 * Decodes a subtitle block payload to the same "text" shape
 * matroska-subtitles emits, so AniSync's existing buildAssDocument
 * client code doesn't need to special-case the source.
 *
 * For S_TEXT/ASS and S_TEXT/SSA the MKV Block payload is the
 * dialogue line with its fields reordered:
 *   ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text
 * matroska-subtitles strips everything up to and including the 8th
 * comma so the consumer just sees the Text portion (including any
 * commas inside Text). We mirror that here.
 *
 * For S_TEXT/UTF8 (plain SRT-style payload) the bytes ARE the text;
 * nothing to strip.
 */
function decodeSubtitle(payload, codecID) {
    const raw = new TextDecoder('utf-8', { fatal: false }).decode(payload);
    if (codecID === 'S_TEXT/ASS' || codecID === 'S_TEXT/SSA') {
        let commas = 0;
        let idx = 0;
        while (idx < raw.length && commas < 8) {
            if (raw.charCodeAt(idx) === 44 /* ',' */) commas++;
            idx++;
        }
        if (commas === 8) return raw.substring(idx);
    }
    return raw;
}

/**
 * CodecPrivate for S_TEXT/ASS is the ASS file header (everything up
 * to but not including the [Events] Dialogue: lines) as UTF-8. For
 * other subtitle codecs the field is usually empty or codec-specific
 * bytes we don't need.
 */
function decodeHeader(codecPrivate, codecID) {
    if (!codecPrivate || codecPrivate.length === 0) return '';
    return new TextDecoder('utf-8', { fatal: false }).decode(codecPrivate);
}

/**
 * Filters a TrackEntry list down to those matching a language hint:
 *
 *   "auto"  — keep only the FIRST track whose language tag starts
 *             with "en" or whose name contains "english"/"eng".
 *             Mirrors the client's auto-promote rule.
 *   "eng"   — keep tracks with language matching the ISO-639-2/B
 *             code (also matches ISO-639-1 "en" via prefix).
 *   anything else — same explicit-code rule; unknown codes filter
 *             to empty (caller handles).
 *
 * Track NAME is checked alongside language so fansubs that left
 * Language="und" but set TrackName="English [GroupName]" still
 * match — same dual-signal the client's auto-promote uses.
 */
function filterTracksByLang(subtitleTracks, lang) {
    const isEnglish = (t) => {
        const l = (t.language || '').toLowerCase();
        const n = (t.name || '').toLowerCase();
        return l.startsWith('en') || /\benglish\b/.test(n) || /\beng\b/.test(n);
    };
    if (lang === 'auto') {
        for (const t of subtitleTracks) {
            if (isEnglish(t)) return [t];
        }
        return [];
    }
    // Explicit ISO code path. Match by language prefix so e.g.
    // lang=spa keeps both "spa" and any "es-*" regional tags.
    const code = lang.toLowerCase();
    const shortCode = code.length >= 2 ? code.substring(0, 2) : code;
    return subtitleTracks.filter(t => {
        const l = (t.language || '').toLowerCase();
        if (!l) return false;
        return l === code || l.startsWith(shortCode);
    });
}
