// EBML / Matroska low-level primitives.
//
// Spec reference: https://www.rfc-editor.org/rfc/rfc8794 (EBML),
// https://www.matroska.org/technical/elements.html (Matroska element IDs).
//
// Two kinds of variable-length integers we care about:
//   * Element IDs       — keep the leading marker bit (it's part of the ID).
//   * Data sizes        — strip the leading marker bit (it's just length prefix).
// Both encode 1..8 bytes; the length is determined by the position of the
// first set bit in byte 0.

/**
 * Reads a variable-length integer starting at `buf[off]`.
 * @param {Uint8Array} buf
 * @param {number} off
 * @param {boolean} keepMarker  true → return the bits as-is (element ID),
 *                              false → strip the leading marker bit (data size).
 * @returns {{ value: number, length: number }}
 */
export function readVint(buf, off, keepMarker) {
    if (off >= buf.length) throw new Error('VINT: read past EOF');
    const first = buf[off];
    if (first === 0) throw new Error('VINT: invalid leading byte 0x00');

    let len = 1;
    let mask = 0x80;
    while (!(first & mask)) {
        mask >>>= 1;
        len++;
        if (len > 8) throw new Error('VINT: length > 8 bytes');
    }
    if (off + len > buf.length) {
        throw new Error('VINT: payload past EOF');
    }

    // JS bitwise ops cap at 32 bits, but Matroska sizes can be larger.
    // Use plain arithmetic for values that may exceed 2^32. We cap at
    // Number.MAX_SAFE_INTEGER which is ~9 PB — far above any real MKV.
    let value = keepMarker ? first : (first & (mask - 1));
    for (let i = 1; i < len; i++) {
        value = value * 256 + buf[off + i];
    }
    return { value, length: len };
}

/**
 * Reads an EBML element header: ID followed by data size.
 * Returns the element's ID, the byte offset where its DATA starts,
 * the data size, and the offset of the element AFTER this one.
 *
 * Handles the "unknown size" sentinel (all data bits set) by returning
 * `size === Infinity`. Callers walking such elements must stop at a
 * known terminating element instead of using `nextOffset`.
 */
export function readElement(buf, off) {
    const id = readVint(buf, off, true);
    const size = readVint(buf, off + id.length, false);

    // EBML unknown-size sentinel: all VINT data bits = 1. For an 8-byte
    // vint that's 0xFFFFFFFFFFFFFF (the marker bit isn't included in the
    // value). This is legal for Segment elements in live recordings.
    const isUnknownSize =
        size.length === 1 ? size.value === 0x7F :
        size.length === 2 ? size.value === 0x3FFF :
        size.length === 3 ? size.value === 0x1FFFFF :
        size.length === 4 ? size.value === 0x0FFFFFFF :
        size.length === 5 ? size.value === 0x07FFFFFFFF :
        size.length === 6 ? size.value === 0x03FFFFFFFFFF :
        size.length === 7 ? size.value === 0x01FFFFFFFFFFFF :
        size.length === 8 ? size.value === 0x00FFFFFFFFFFFFFF :
        false;

    const dataOffset = off + id.length + size.length;
    return {
        id: id.value,
        size: isUnknownSize ? Infinity : size.value,
        unknownSize: isUnknownSize,
        dataOffset,
        nextOffset: isUnknownSize ? Infinity : dataOffset + size.value,
    };
}

/** Reads an unsigned big-endian integer of 1..8 bytes. */
export function readUInt(buf, off, len) {
    let v = 0;
    for (let i = 0; i < len; i++) v = v * 256 + buf[off + i];
    return v;
}

/** Reads a signed big-endian integer (two's complement) of 1..8 bytes. */
export function readSInt(buf, off, len) {
    if (len === 0) return 0;
    let v = readUInt(buf, off, len);
    const sign = 1 << ((len * 8) - 1);
    if (v & sign) v -= 2 ** (len * 8);
    return v;
}

/** Reads a UTF-8 string of `len` bytes (trailing nulls stripped). */
export function readString(buf, off, len) {
    let end = off + len;
    while (end > off && buf[end - 1] === 0) end--;
    return new TextDecoder('utf-8', { fatal: false }).decode(buf.slice(off, end));
}

/** Returns the slice as a fresh Uint8Array (so callers can keep it around). */
export function readBytes(buf, off, len) {
    return buf.slice(off, off + len);
}

// Matroska element IDs we care about. Names match the spec for grep-ability.
export const ID = {
    EBML:                0x1A45DFA3,
    Segment:             0x18538067,

    SeekHead:            0x114D9B74,
    Seek:                0x4DBB,
    SeekID:              0x53AB,
    SeekPosition:        0x53AC,

    SegmentInfo:         0x1549A966,
    TimecodeScale:       0x2AD7B1,
    Duration:            0x4489,

    Tracks:              0x1654AE6B,
    TrackEntry:          0xAE,
    TrackNumber:         0xD7,
    TrackType:           0x83,
    CodecID:             0x86,
    CodecPrivate:        0x63A2,
    Name:                0x536E,
    Language:            0x22B59C,
    FlagDefault:         0x88,
    FlagForced:          0x55AA,

    Cluster:             0x1F43B675,
    ClusterTimecode:     0xE7,
    SimpleBlock:         0xA3,
    BlockGroup:          0xA0,
    Block:               0xA1,
    BlockDuration:       0x9B,

    Cues:                0x1C53BB6B,
    CuePoint:            0xBB,
    CueTime:             0xB3,
    CueTrackPositions:   0xB7,
    CueTrack:            0xF7,
    CueClusterPosition:  0xF1,
    CueRelativePosition: 0xF0,

    Void:                0xEC,
};

// Track types per spec — only the one we care about.
export const TRACK_TYPE_SUBTITLE = 0x11;
