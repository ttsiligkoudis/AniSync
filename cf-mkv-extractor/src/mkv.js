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

// Initial head read — big enough to cover EBML header + Segment start +
// usually the entire SeekHead. SeekHead is small (~few hundred bytes).
const HEAD_BYTES = 256 * 1024;
// When Tracks is referenced via SeekHead we range-fetch this much from
// the Tracks offset. Tracks element is usually small but CodecPrivate
// for ASS tracks can be a few KB each across multiple tracks.
const TRACKS_FETCH = 256 * 1024;
// Cues are usually proportional to file length — a few hundred KB up
// to a couple of MB. 4 MB covers the worst case we've seen.
const CUES_FETCH = 4 * 1024 * 1024;
// Per-cluster fetch. Subtitle-only clusters are tiny; clusters with
// video frames can be a few hundred KB. 1 MB is generous and gives
// the parser room to skip non-sub blocks without re-fetching.
const CLUSTER_FETCH = 1 * 1024 * 1024;
// Conservative cap on total bytes we'll pull, so a pathological MKV
// can't drain the user's RD quota even if our parser misbehaves.
const MAX_TOTAL_FETCH = 60 * 1024 * 1024;

export async function extractSubtitles(reader) {
    await reader.probeSize();

    // ── 1. Read the file head ─────────────────────────────────────
    const head = await reader.read(0, HEAD_BYTES);

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
    const tracksBuf = await reader.read(tracksAbs, TRACKS_FETCH);
    const tracksEl = readElement(tracksBuf, 0);
    if (tracksEl.id !== ID.Tracks) {
        throw new Error(`expected Tracks at ${tracksAbs}, got id 0x${tracksEl.id.toString(16)}`);
    }
    const tracks = parseTracks(tracksBuf, tracksEl.dataOffset, tracksEl.dataOffset + tracksEl.size);
    const subtitleTracks = tracks.filter(t => t.type === TRACK_TYPE_SUBTITLE && t.codecID);
    if (subtitleTracks.length === 0) {
        return { tracks: [] };
    }
    const subTrackNumbers = new Set(subtitleTracks.map(t => t.number));

    // ── 5. Read Cues, collect subtitle Cluster positions ──────────
    const cuesOff = seekOffsets[ID.Cues];
    if (cuesOff === undefined) {
        throw new Error('SeekHead has no Cues pointer — file has no index');
    }
    const cuesAbs = absoluteFromSegment(cuesOff);
    const cuesBuf = await reader.read(cuesAbs, CUES_FETCH);
    const cuesEl = readElement(cuesBuf, 0);
    if (cuesEl.id !== ID.Cues) {
        throw new Error(`expected Cues at ${cuesAbs}, got id 0x${cuesEl.id.toString(16)}`);
    }
    const cuePositions = parseCues(cuesBuf, cuesEl.dataOffset, cuesEl.dataOffset + cuesEl.size, subTrackNumbers);
    // Unique cluster offsets — same cluster often has cues for multiple tracks.
    const clusterAbsOffsets = Array.from(new Set(
        cuePositions.map(cp => absoluteFromSegment(cp.clusterPosition))
    )).sort((a, b) => a - b);

    // ── 6. For each Cluster, pull subtitle blocks ─────────────────
    const cuesByTrack = new Map(subtitleTracks.map(t => [t.number, []]));
    let totalFetched = HEAD_BYTES + TRACKS_FETCH + CUES_FETCH;

    for (const clusterAbs of clusterAbsOffsets) {
        if (totalFetched >= MAX_TOTAL_FETCH) break;
        const clusterBuf = await reader.read(clusterAbs, CLUSTER_FETCH);
        totalFetched += CLUSTER_FETCH;
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
            // If the element body would extend past what we fetched,
            // skip — sub blocks are small and fit in our slab; video
            // blocks routinely don't but we wouldn't decode them anyway.
            if (child.size === Infinity || child.dataOffset + child.size > clusterBuf.length) {
                childOff = child.dataOffset + Math.min(child.size, clusterBuf.length - child.dataOffset);
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
