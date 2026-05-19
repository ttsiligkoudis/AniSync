//! EBML / Matroska low-level primitives.
//!
//! Port of cf-mkv-extractor/src/ebml.js. Same VINT semantics, same
//! element-ID constants. Where the JS version constructs short-lived
//! objects per element, this version returns plain structs by value
//! — no allocations in the hot path, which is the whole point of
//! moving to Rust for the cluster-walk loop.

#[derive(Debug, Clone, Copy)]
pub struct EbmlElement {
    pub id: u64,
    pub size: u64,
    pub data_offset: usize,
    pub next_offset: usize,
}

pub const TRACK_TYPE_SUBTITLE: u64 = 0x11;

// Element IDs we care about. Same values as ebml.js. Kept as u64
// because EBML IDs are VINTs with the marker bit retained.
pub mod id {
    pub const EBML_HEADER: u64 = 0x1A45_DFA3;
    pub const SEGMENT: u64 = 0x1853_8067;

    pub const SEEK_HEAD: u64 = 0x114D_9B74;
    pub const SEEK: u64 = 0x4DBB;
    pub const SEEK_ID: u64 = 0x53AB;
    pub const SEEK_POSITION: u64 = 0x53AC;

    pub const SEGMENT_INFO: u64 = 0x1549_A966;
    pub const TIMECODE_SCALE: u64 = 0x2AD7_B1;

    pub const TRACKS: u64 = 0x1654_AE6B;
    pub const TRACK_ENTRY: u64 = 0xAE;
    pub const TRACK_NUMBER: u64 = 0xD7;
    pub const TRACK_TYPE: u64 = 0x83;
    pub const CODEC_ID: u64 = 0x86;
    pub const CODEC_PRIVATE: u64 = 0x63A2;
    pub const NAME: u64 = 0x536E;
    pub const LANGUAGE: u64 = 0x22B5_9C;

    pub const CUES: u64 = 0x1C53_BB6B;
    pub const CUE_POINT: u64 = 0xBB;
    pub const CUE_TIME: u64 = 0xB3;
    pub const CUE_TRACK_POSITIONS: u64 = 0xB7;
    pub const CUE_TRACK: u64 = 0xF7;
    pub const CUE_CLUSTER_POSITION: u64 = 0xF1;

    pub const CLUSTER: u64 = 0x1F43_B675;
    pub const CLUSTER_TIMECODE: u64 = 0xE7;
    pub const SIMPLE_BLOCK: u64 = 0xA3;
    pub const BLOCK_GROUP: u64 = 0xA0;
    pub const BLOCK: u64 = 0xA1;
    pub const BLOCK_DURATION: u64 = 0x9B;
}

#[derive(Debug, Clone, Copy)]
pub struct Vint {
    pub value: u64,
    pub length: usize,
}

/// Reads a variable-length integer starting at buf[off].
/// keep_marker=true keeps the leading bit set (element IDs);
/// false strips it (data sizes).
pub fn read_vint(buf: &[u8], off: usize, keep_marker: bool) -> Result<Vint, &'static str> {
    if off >= buf.len() {
        return Err("VINT: read past EOF");
    }
    let first = buf[off];
    if first == 0 {
        return Err("VINT: invalid leading byte 0x00");
    }
    let mut len = 1usize;
    let mut mask = 0x80u8;
    while (first & mask) == 0 {
        mask >>= 1;
        len += 1;
        if len > 8 {
            return Err("VINT: length > 8 bytes");
        }
    }
    if off + len > buf.len() {
        return Err("VINT: payload past EOF");
    }
    let mut value: u64 = if keep_marker {
        first as u64
    } else {
        (first & !mask) as u64
    };
    for i in 1..len {
        value = (value << 8) | (buf[off + i] as u64);
    }
    Ok(Vint { value, length: len })
}

/// Reads an EBML element header at buf[off] — both ID and size VINTs.
pub fn read_element(buf: &[u8], off: usize) -> Result<EbmlElement, &'static str> {
    let id_v = read_vint(buf, off, true)?;
    let size_v = read_vint(buf, off + id_v.length, false)?;
    let data_offset = off + id_v.length + size_v.length;

    // EBML "unknown size" = all 1s after the marker. Treat as
    // max-u64 so callers can recognise and bail.
    let max_for_len = (1u64 << (7 * size_v.length as u32)) - 1;
    let size = if size_v.value == max_for_len {
        u64::MAX
    } else {
        size_v.value
    };
    let next_offset = if size == u64::MAX {
        usize::MAX
    } else {
        data_offset + size as usize
    };
    Ok(EbmlElement { id: id_v.value, size, data_offset, next_offset })
}

pub fn read_uint(buf: &[u8], off: usize, size: u64) -> u64 {
    let mut v = 0u64;
    let end = (off + size as usize).min(buf.len());
    for i in off..end {
        v = (v << 8) | (buf[i] as u64);
    }
    v
}

pub fn read_string(buf: &[u8], off: usize, size: u64) -> String {
    let end = (off + size as usize).min(buf.len());
    let slice = &buf[off..end];
    let trimmed = slice
        .iter()
        .position(|&b| b == 0)
        .map(|i| &slice[..i])
        .unwrap_or(slice);
    String::from_utf8_lossy(trimmed).into_owned()
}

pub fn read_bytes(buf: &[u8], off: usize, size: u64) -> Vec<u8> {
    let end = (off + size as usize).min(buf.len());
    buf[off..end].to_vec()
}

/// Read a signed 16-bit big-endian integer at buf[off].
/// (SimpleBlock timecode field.)
pub fn read_i16_be(buf: &[u8], off: usize) -> i16 {
    if off + 2 > buf.len() {
        return 0;
    }
    ((buf[off] as i16) << 8) | (buf[off + 1] as i16)
}

/// Walks a contiguous EBML buffer scanning for a direct-child
/// element with the requested ID. Returns the element header,
/// or None if not found within the bounds.
pub fn find_child(buf: &[u8], start: usize, end: usize, target_id: u64) -> Option<EbmlElement> {
    let mut off = start;
    while off < end && off < buf.len() {
        let el = read_element(buf, off).ok()?;
        if el.id == target_id {
            return Some(el);
        }
        if el.size == u64::MAX {
            return None;
        }
        off = el.next_offset;
        if off == usize::MAX {
            return None;
        }
    }
    None
}
