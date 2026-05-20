//! Indexed Matroska subtitle extractor — Rust/WASM port of mkv.js.
//!
//! Same overall flow:
//!   1. Read file head (256 KB).
//!   2. Locate Segment, then SeekHead inside Segment.
//!   3. Resolve Tracks → identify subtitle TrackEntries
//!      (filtered by ?lang= if provided).
//!   4. Resolve Cues → collect cluster positions for those tracks.
//!   5. Coalesce nearby cluster offsets into Range batches.
//!   6. Fetch each batch, walk its clusters, pull subtitle Blocks.
//!   7. Return per-track cue arrays + ASS header.
//!
//! Shard semantics: when shards>1, this invocation processes only
//! the (shard)-th contiguous 1/N slice of the cluster-offset list.
//! Same as the JS worker.

use crate::ebml::{
    self, find_child, id, read_bytes, read_element, read_i16_be, read_string, read_uint,
    read_vint, EbmlElement, TRACK_TYPE_SUBTITLE,
};
use crate::reader::RangeReader;
use serde::Serialize;
use std::collections::{HashMap, HashSet};
use std::time::Duration;
use worker::*;

// Buffer sizing. See mkv.js for the rationale on each value; the
// Rust port keeps the same numbers so behaviour is comparable.
const HEAD_BYTES: u64 = 256 * 1024;
const TRACKS_FETCH: u64 = 256 * 1024;
const CUES_FETCH: u64 = 256 * 1024;
const CLUSTER_FETCH: u64 = 4 * 1024 * 1024;
const CLUSTER_BATCH_GAP: u64 = 4 * 1024 * 1024;
const MAX_BATCH_SIZE: u64 = 6 * 1024 * 1024;
const MAX_CLUSTER_BATCHES: usize = 30;
const MAX_TOTAL_FETCH: u64 = 50 * 1024 * 1024;
const BATCH_PACING_MS: u64 = 100;

// Single-shot CPU-budget escape hatch. Initially I set this at 80
// on the bet that Rust would be 4× faster than JS at the cluster
// walk. Real-world testing on CF Free shows wasm-bindgen's per-
// fetch JS-bridge overhead eats most of that advantage — we're
// barely faster than JS per cluster, not 4× faster. Match the JS
// worker's 20-cluster ceiling so the same files trigger sharding.
const SINGLE_SHOT_CLUSTER_CEILING: usize = 20;

#[derive(Serialize, Clone)]
pub struct TrackOutput {
    pub number: u64,
    pub language: String,
    pub name: String,
    #[serde(rename = "codecID")]
    pub codec_id: String,
    pub header: String,
    pub cues: Vec<CueOutput>,
}

#[derive(Serialize, Clone)]
pub struct CueOutput {
    pub time: f64,
    pub duration: f64,
    pub text: String,
}

#[derive(Serialize)]
pub struct ExtractResult {
    pub tracks: Vec<TrackOutput>,
    pub truncated: bool,
    pub shard: u32,
    pub shards: u32,
    #[serde(rename = "clustersTotal")]
    pub clusters_total: usize,
    #[serde(rename = "clustersInShard")]
    pub clusters_in_shard: usize,
}

#[derive(Default)]
pub struct ExtractOptions {
    pub lang: Option<String>,
    pub shards: u32,
    pub shard: u32,
}

struct TrackEntry {
    number: u64,
    track_type: u64,
    codec_id: String,
    codec_private: Option<Vec<u8>>,
    name: String,
    language: String,
}

struct CuePosition {
    track: u64,
    cluster_position: u64,
}

struct SimpleBlockHeader {
    track_number: u64,
    timecode: i64,
    payload_offset: usize,
    payload_size: usize,
}

pub async fn extract_subtitles(reader: &mut RangeReader, opts: &ExtractOptions) -> Result<ExtractResult> {
    reader.probe_size().await?;
    let total_size = reader.total_size.unwrap_or(0);
    console_log!(
        "[extract] file totalSize={} shards={} shard={}",
        total_size, opts.shards, opts.shard
    );

    // ── 1. Read the file head ─────────────────────────────────────
    let head = reader.read_critical(0, HEAD_BYTES).await?;

    // ── 2. Skip EBML header, locate Segment ───────────────────────
    let ebml_el = read_element(&head, 0).map_err(|e| Error::from(format!("EBML: {}", e)))?;
    if ebml_el.id != id::EBML_HEADER {
        return Err(Error::from("not a Matroska file (no EBML header at byte 0)"));
    }
    let segment = read_element(&head, ebml_el.next_offset)
        .map_err(|e| Error::from(format!("Segment: {}", e)))?;
    if segment.id != id::SEGMENT {
        return Err(Error::from("expected Segment element after EBML header"));
    }
    let segment_data_start = segment.data_offset;

    // ── 3. Locate SeekHead inside the Segment ─────────────────────
    let seek_head = find_child(&head, segment_data_start, head.len(), id::SEEK_HEAD)
        .ok_or_else(|| Error::from("no SeekHead — file lacks an index"))?;
    let seek_offsets = parse_seek_head(&head, seek_head.data_offset, seek_head.size as usize);

    // SegmentInfo for TimecodeScale (ns per timecode unit, default 1e6).
    let mut timecode_scale: u64 = 1_000_000;
    if let Some(&info_off) = seek_offsets.get(&id::SEGMENT_INFO) {
        let info_abs = segment_data_start + info_off as usize;
        let info_buf: Vec<u8> = if info_abs + 4096 <= head.len() {
            head.clone()
        } else {
            reader.read(info_abs as u64, 4096).await?
        };
        let local_off = if info_abs + 4096 <= head.len() { info_abs } else { 0 };
        if let Ok(info_el) = read_element(&info_buf, local_off) {
            if info_el.id == id::SEGMENT_INFO {
                let end = info_el.data_offset + info_el.size as usize;
                if let Some(ts) = find_child(&info_buf, info_el.data_offset, end, id::TIMECODE_SCALE) {
                    timecode_scale = read_uint(&info_buf, ts.data_offset, ts.size);
                }
            }
        }
    }

    // ── 4. Read Tracks, identify subtitle TrackEntries ────────────
    let tracks_off = seek_offsets
        .get(&id::TRACKS)
        .copied()
        .ok_or_else(|| Error::from("SeekHead has no Tracks pointer"))?;
    let tracks_abs = segment_data_start as u64 + tracks_off;
    let tracks_buf = reader.read_critical(tracks_abs, TRACKS_FETCH).await?;
    let tracks_el = read_element(&tracks_buf, 0)
        .map_err(|e| Error::from(format!("Tracks: {}", e)))?;
    if tracks_el.id != id::TRACKS {
        return Err(Error::from(format!(
            "expected Tracks at {}, got id 0x{:x}",
            tracks_abs, tracks_el.id
        )));
    }
    let tracks_end = tracks_el.data_offset + tracks_el.size as usize;
    let tracks_all = parse_tracks(&tracks_buf, tracks_el.data_offset, tracks_end);

    let mut subtitle_tracks: Vec<TrackEntry> = tracks_all
        .into_iter()
        .filter(|t| t.track_type == TRACK_TYPE_SUBTITLE && !t.codec_id.is_empty())
        .collect();
    if subtitle_tracks.is_empty() {
        return Ok(ExtractResult {
            tracks: Vec::new(),
            truncated: false,
            shard: opts.shard,
            shards: opts.shards.max(1),
            clusters_total: 0,
            clusters_in_shard: 0,
        });
    }

    // Optional language filter.
    if let Some(lang) = opts.lang.as_deref() {
        subtitle_tracks = filter_tracks_by_lang(subtitle_tracks, lang);
        if subtitle_tracks.is_empty() {
            return Ok(ExtractResult {
                tracks: Vec::new(),
                truncated: false,
                shard: opts.shard,
                shards: opts.shards.max(1),
                clusters_total: 0,
                clusters_in_shard: 0,
            });
        }
    }
    let sub_track_numbers: HashSet<u64> = subtitle_tracks.iter().map(|t| t.number).collect();

    // ── 5. Read Cues, collect subtitle cluster positions ──────────
    let cues_off = seek_offsets
        .get(&id::CUES)
        .copied()
        .ok_or_else(|| Error::from("SeekHead has no Cues pointer — file has no index"))?;
    let cues_abs = segment_data_start as u64 + cues_off;
    console_log!("[extract] cues at absolute offset={} (totalSize={})", cues_abs, total_size);
    let mut cues_buf = reader.read_critical(cues_abs, CUES_FETCH).await?;
    let cues_el = read_element(&cues_buf, 0)
        .map_err(|e| Error::from(format!("Cues: {}", e)))?;
    if cues_el.id != id::CUES {
        return Err(Error::from(format!(
            "expected Cues at {}, got id 0x{:x}",
            cues_abs, cues_el.id
        )));
    }
    let cues_needed = cues_el.data_offset + cues_el.size as usize;
    if cues_needed > cues_buf.len() {
        // Top-up: the cues element extends past our initial fetch.
        let extra_start = cues_abs + cues_buf.len() as u64;
        let extra_len = (cues_needed - cues_buf.len()) as u64;
        console_log!(
            "[cues] element is {} bytes, initial fetch was {}; topping up {} bytes",
            cues_el.size, cues_buf.len(), extra_len
        );
        let extra = reader.read_critical(extra_start, extra_len).await?;
        let mut combined = Vec::with_capacity(cues_needed);
        combined.extend_from_slice(&cues_buf);
        combined.extend_from_slice(&extra);
        cues_buf = combined;
    }
    let cue_positions = parse_cues(
        &cues_buf,
        cues_el.data_offset,
        cues_needed,
        &sub_track_numbers,
    );
    let mut cluster_abs_offsets: Vec<u64> = cue_positions
        .iter()
        .map(|cp| segment_data_start as u64 + cp.cluster_position)
        .collect::<HashSet<_>>()
        .into_iter()
        .collect();
    cluster_abs_offsets.sort_unstable();

    let shards = opts.shards.max(1);
    let shard_idx = opts.shard.min(shards.saturating_sub(1));
    console_log!(
        "[extract] clusters={} ceiling={} shards={} shard={}",
        cluster_abs_offsets.len(),
        SINGLE_SHOT_CLUSTER_CEILING,
        shards,
        shard_idx
    );

    // Free-tier CPU escape hatch. Same as the JS worker.
    if cluster_abs_offsets.len() > SINGLE_SHOT_CLUSTER_CEILING && shards == 1 {
        console_log!(
            "[extract] over ceiling on single-shot; returning truncated for client shard escalation"
        );
        return Ok(ExtractResult {
            tracks: subtitle_tracks
                .iter()
                .map(|t| TrackOutput {
                    number: t.number,
                    language: if t.language.is_empty() { "und".into() } else { t.language.clone() },
                    name: t.name.clone(),
                    codec_id: t.codec_id.clone(),
                    header: decode_header(t.codec_private.as_deref(), &t.codec_id),
                    cues: Vec::new(),
                })
                .collect(),
            truncated: true,
            shard: 0,
            shards: 1,
            clusters_total: cluster_abs_offsets.len(),
            clusters_in_shard: 0,
        });
    }

    // Shard slicing: contiguous 1/N slice of the cluster offsets.
    let active_offsets: Vec<u64> = if shards > 1 {
        let slice_size = (cluster_abs_offsets.len() + shards as usize - 1) / shards as usize;
        let start = (shard_idx as usize) * slice_size;
        let end = (start + slice_size).min(cluster_abs_offsets.len());
        cluster_abs_offsets[start..end].to_vec()
    } else {
        cluster_abs_offsets.clone()
    };

    // ── 6. For each Cluster, pull subtitle blocks ─────────────────
    let mut cues_by_track: HashMap<u64, Vec<CueOutput>> =
        subtitle_tracks.iter().map(|t| (t.number, Vec::new())).collect();
    let mut total_fetched: u64 = HEAD_BYTES + TRACKS_FETCH + CUES_FETCH;
    let mut truncated = false;

    // Greedy batch coalescing — mirrors the JS algorithm.
    #[derive(Debug)]
    struct Batch {
        start: u64,
        end: u64,
    }
    let mut batches: Vec<Batch> = Vec::new();
    for &off in &active_offsets {
        let new_end = off + CLUSTER_FETCH;
        if let Some(cur) = batches.last_mut() {
            if off.saturating_sub(cur.end) < CLUSTER_BATCH_GAP
                && new_end.saturating_sub(cur.start) <= MAX_BATCH_SIZE
            {
                cur.end = new_end;
                continue;
            }
        }
        batches.push(Batch { start: off, end: new_end });
    }
    // Coarsen if we still have more batches than the cap.
    while batches.len() > MAX_CLUSTER_BATCHES {
        let mut merge_idx: Option<usize> = None;
        let mut min_gap: u64 = u64::MAX;
        for i in 0..batches.len() - 1 {
            let merged = batches[i + 1].end - batches[i].start;
            if merged > MAX_BATCH_SIZE {
                continue;
            }
            let gap = batches[i + 1].start - batches[i].end;
            if gap < min_gap {
                min_gap = gap;
                merge_idx = Some(i);
            }
        }
        match merge_idx {
            Some(i) => {
                batches[i].end = batches[i + 1].end;
                batches.remove(i + 1);
            }
            None => break,
        }
    }

    // Group cluster offsets by their containing batch.
    let clusters_in_batch: Vec<Vec<u64>> = batches
        .iter()
        .map(|b| {
            active_offsets
                .iter()
                .copied()
                .filter(|&off| off >= b.start && off + CLUSTER_FETCH <= b.end)
                .collect()
        })
        .collect();

    let batches_count = batches.len();
    for (bi, b) in batches.iter().enumerate() {
        let len = b.end - b.start;
        if total_fetched + len > MAX_TOTAL_FETCH {
            truncated = true;
            break;
        }
        if reader.read(b.start, len).await.is_err() {
            continue;
        }
        total_fetched += len;

        for &cluster_abs in &clusters_in_batch[bi] {
            let cluster_buf = match reader.read(cluster_abs, CLUSTER_FETCH).await {
                Ok(b) => b,
                Err(_) => continue,
            };
            let cluster_el = match read_element(&cluster_buf, 0) {
                Ok(e) => e,
                Err(_) => continue,
            };
            if cluster_el.id != id::CLUSTER {
                continue;
            }
            let cluster_size_in_buf = if cluster_el.size == u64::MAX {
                cluster_buf.len() - cluster_el.data_offset
            } else {
                (cluster_el.size as usize).min(cluster_buf.len() - cluster_el.data_offset)
            };
            let cluster_end = cluster_el.data_offset + cluster_size_in_buf;

            // Cluster's base timecode.
            let cluster_timecode: u64 = find_child(
                &cluster_buf,
                cluster_el.data_offset,
                cluster_end,
                id::CLUSTER_TIMECODE,
            )
            .map(|tc| read_uint(&cluster_buf, tc.data_offset, tc.size))
            .unwrap_or(0);

            // Walk every direct child, picking out SimpleBlock /
            // BlockGroup elements whose track is a subtitle.
            let mut child_off = cluster_el.data_offset;
            while child_off < cluster_end {
                let child = match read_element(&cluster_buf, child_off) {
                    Ok(e) => e,
                    Err(_) => break,
                };
                if child.size == u64::MAX {
                    break;
                }
                if child.data_offset + child.size as usize > cluster_buf.len() {
                    child_off = child.data_offset + child.size as usize;
                    if child_off >= cluster_end {
                        break;
                    }
                    continue;
                }

                if child.id == id::SIMPLE_BLOCK {
                    // Fast-skip: peek track-number VINT before
                    // full parse. Non-subtitle blocks (most of
                    // them) cost just a byte read + Set lookup.
                    let track_number = peek_track_number(&cluster_buf, child.data_offset);
                    if sub_track_numbers.contains(&track_number) {
                        if let Some(sb) = parse_simple_block(
                            &cluster_buf,
                            child.data_offset,
                            child.size as usize,
                        ) {
                            push_cue(
                                &mut cues_by_track,
                                &subtitle_tracks,
                                &sb,
                                &cluster_buf,
                                cluster_timecode,
                                timecode_scale,
                                4000.0, // default duration
                            );
                        }
                    }
                } else if child.id == id::BLOCK_GROUP {
                    parse_block_group(
                        &cluster_buf,
                        child.data_offset,
                        child.size as usize,
                        &sub_track_numbers,
                        &subtitle_tracks,
                        cluster_timecode,
                        timecode_scale,
                        &mut cues_by_track,
                    );
                }

                child_off = child.data_offset + child.size as usize;
            }
        }

        reader.evict(b.start, b.end);

        if bi + 1 < batches_count {
            Delay::from(std::time::Duration::from_millis(BATCH_PACING_MS)).await;
        }
    }

    // ── 7. Stitch tracks together ─────────────────────────────────
    let tracks_out: Vec<TrackOutput> = subtitle_tracks
        .iter()
        .map(|t| TrackOutput {
            number: t.number,
            language: if t.language.is_empty() { "und".into() } else { t.language.clone() },
            name: t.name.clone(),
            codec_id: t.codec_id.clone(),
            header: decode_header(t.codec_private.as_deref(), &t.codec_id),
            cues: cues_by_track.remove(&t.number).unwrap_or_default(),
        })
        .collect();

    Ok(ExtractResult {
        tracks: tracks_out,
        truncated,
        shard: shard_idx,
        shards,
        clusters_total: cluster_abs_offsets.len(),
        clusters_in_shard: active_offsets.len(),
    })
}

// ─── helpers ──────────────────────────────────────────────────────

fn parse_seek_head(buf: &[u8], start: usize, size: usize) -> HashMap<u64, u64> {
    let mut out = HashMap::new();
    let mut off = start;
    let end = (start + size).min(buf.len());
    while off < end {
        let el = match read_element(buf, off) {
            Ok(e) => e,
            Err(_) => break,
        };
        if el.id == id::SEEK {
            let seek_end = el.data_offset + el.size as usize;
            let mut sub_off = el.data_offset;
            let mut id_val: Option<u64> = None;
            let mut pos_val: Option<u64> = None;
            while sub_off < seek_end && sub_off < buf.len() {
                let sub = match read_element(buf, sub_off) {
                    Ok(e) => e,
                    Err(_) => break,
                };
                match sub.id {
                    id::SEEK_ID => {
                        // Read the inner ID as raw bytes (it's a
                        // VINT with the marker preserved).
                        if let Ok(v) = read_vint(buf, sub.data_offset, true) {
                            id_val = Some(v.value);
                        }
                    }
                    id::SEEK_POSITION => {
                        pos_val = Some(read_uint(buf, sub.data_offset, sub.size));
                    }
                    _ => {}
                }
                sub_off = sub.next_offset;
            }
            if let (Some(i), Some(p)) = (id_val, pos_val) {
                out.insert(i, p);
            }
        }
        off = el.next_offset;
    }
    out
}

fn parse_tracks(buf: &[u8], start: usize, end: usize) -> Vec<TrackEntry> {
    let mut out = Vec::new();
    let mut off = start;
    while off < end && off < buf.len() {
        let el = match read_element(buf, off) {
            Ok(e) => e,
            Err(_) => break,
        };
        if el.id == id::TRACK_ENTRY {
            let entry_end = el.data_offset + el.size as usize;
            out.push(parse_track_entry(buf, el.data_offset, entry_end));
        }
        off = el.next_offset;
    }
    out
}

fn parse_track_entry(buf: &[u8], start: usize, end: usize) -> TrackEntry {
    let mut t = TrackEntry {
        number: 0,
        track_type: 0,
        codec_id: String::new(),
        codec_private: None,
        name: String::new(),
        language: String::new(),
    };
    let mut off = start;
    while off < end && off < buf.len() {
        let el = match read_element(buf, off) {
            Ok(e) => e,
            Err(_) => break,
        };
        if el.data_offset + el.size as usize > buf.len() {
            break;
        }
        match el.id {
            id::TRACK_NUMBER => t.number = read_uint(buf, el.data_offset, el.size),
            id::TRACK_TYPE => t.track_type = read_uint(buf, el.data_offset, el.size),
            id::CODEC_ID => t.codec_id = read_string(buf, el.data_offset, el.size),
            id::CODEC_PRIVATE => t.codec_private = Some(read_bytes(buf, el.data_offset, el.size)),
            id::NAME => t.name = read_string(buf, el.data_offset, el.size),
            id::LANGUAGE => t.language = read_string(buf, el.data_offset, el.size),
            _ => {}
        }
        off = el.next_offset;
    }
    t
}

fn parse_cues(buf: &[u8], start: usize, end: usize, sub_track_set: &HashSet<u64>) -> Vec<CuePosition> {
    let mut out = Vec::new();
    let mut off = start;
    while off < end && off < buf.len() {
        let cp = match read_element(buf, off) {
            Ok(e) => e,
            Err(_) => break,
        };
        if cp.id == id::CUE_POINT {
            let cp_end = cp.data_offset + cp.size as usize;
            let mut pos = cp.data_offset;
            let mut track_positions: Vec<CuePosition> = Vec::new();
            while pos < cp_end && pos < buf.len() {
                let inner = match read_element(buf, pos) {
                    Ok(e) => e,
                    Err(_) => break,
                };
                match inner.id {
                    id::CUE_TIME => {
                        // we don't currently use cue time
                    }
                    id::CUE_TRACK_POSITIONS => {
                        if let Some(tp) = parse_cue_track_positions(
                            buf,
                            inner.data_offset,
                            inner.data_offset + inner.size as usize,
                        ) {
                            if sub_track_set.contains(&tp.track) {
                                track_positions.push(tp);
                            }
                        }
                    }
                    _ => {}
                }
                pos = inner.next_offset;
            }
            out.extend(track_positions);
        }
        if cp.next_offset > buf.len() {
            break;
        }
        off = cp.next_offset;
    }
    out
}

fn parse_cue_track_positions(buf: &[u8], start: usize, end: usize) -> Option<CuePosition> {
    let mut track = 0u64;
    let mut cluster_position: Option<u64> = None;
    let mut off = start;
    while off < end && off < buf.len() {
        let el = match read_element(buf, off) {
            Ok(e) => e,
            Err(_) => break,
        };
        match el.id {
            id::CUE_TRACK => track = read_uint(buf, el.data_offset, el.size),
            id::CUE_CLUSTER_POSITION => cluster_position = Some(read_uint(buf, el.data_offset, el.size)),
            _ => {}
        }
        off = el.next_offset;
    }
    cluster_position.map(|p| CuePosition { track, cluster_position: p })
}

fn peek_track_number(buf: &[u8], off: usize) -> u64 {
    // 1-byte VINT covers tracks 1-127 (every real-world file).
    if off >= buf.len() {
        return 0;
    }
    let fb = buf[off];
    if (fb & 0x80) != 0 {
        return (fb & 0x7F) as u64;
    }
    // Multi-byte fallback.
    read_vint(buf, off, false).map(|v| v.value).unwrap_or(0)
}

fn parse_simple_block(buf: &[u8], off: usize, size: usize) -> Option<SimpleBlockHeader> {
    if size < 4 {
        return None;
    }
    let mut p = off;
    let tn = read_vint(buf, p, false).ok()?;
    p += tn.length;
    let timecode = read_i16_be(buf, p) as i64;
    p += 2;
    // flags byte
    p += 1;
    let payload_end = off + size;
    if p > payload_end {
        return None;
    }
    Some(SimpleBlockHeader {
        track_number: tn.value,
        timecode,
        payload_offset: p,
        payload_size: payload_end - p,
    })
}

#[allow(clippy::too_many_arguments)]
fn parse_block_group(
    buf: &[u8],
    start: usize,
    size: usize,
    sub_track_numbers: &HashSet<u64>,
    subtitle_tracks: &[TrackEntry],
    cluster_timecode: u64,
    timecode_scale: u64,
    cues_by_track: &mut HashMap<u64, Vec<CueOutput>>,
) {
    let end = start + size;
    let mut block: Option<SimpleBlockHeader> = None;
    let mut duration_raw: Option<u64> = None;
    let mut off = start;
    while off < end && off < buf.len() {
        let el = match read_element(buf, off) {
            Ok(e) => e,
            Err(_) => break,
        };
        match el.id {
            id::BLOCK => {
                block = parse_simple_block(buf, el.data_offset, el.size as usize);
            }
            id::BLOCK_DURATION => {
                duration_raw = Some(read_uint(buf, el.data_offset, el.size));
            }
            _ => {}
        }
        off = el.next_offset;
    }
    if let Some(sb) = block {
        if sub_track_numbers.contains(&sb.track_number) {
            let duration_ms = duration_raw
                .map(|d| (d as f64) * (timecode_scale as f64 / 1e6))
                .unwrap_or(4000.0);
            push_cue(
                cues_by_track,
                subtitle_tracks,
                &sb,
                buf,
                cluster_timecode,
                timecode_scale,
                duration_ms,
            );
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn push_cue(
    cues_by_track: &mut HashMap<u64, Vec<CueOutput>>,
    subtitle_tracks: &[TrackEntry],
    sb: &SimpleBlockHeader,
    buf: &[u8],
    cluster_timecode: u64,
    timecode_scale: u64,
    duration_ms: f64,
) {
    let track = subtitle_tracks.iter().find(|t| t.number == sb.track_number);
    let time = (cluster_timecode as f64 + sb.timecode as f64) * (timecode_scale as f64 / 1e6);
    let text = decode_subtitle(
        &buf[sb.payload_offset..sb.payload_offset + sb.payload_size],
        track.map(|t| t.codec_id.as_str()),
    );
    if let Some(bucket) = cues_by_track.get_mut(&sb.track_number) {
        bucket.push(CueOutput { time, duration: duration_ms, text });
    }
}

fn decode_subtitle(payload: &[u8], _codec_id: Option<&str>) -> String {
    String::from_utf8_lossy(payload).into_owned()
}

fn decode_header(codec_private: Option<&[u8]>, codec_id: &str) -> String {
    match (codec_private, codec_id) {
        (Some(bytes), "S_TEXT/ASS") | (Some(bytes), "S_TEXT/SSA") => {
            String::from_utf8_lossy(bytes).into_owned()
        }
        _ => String::new(),
    }
}

fn filter_tracks_by_lang(tracks: Vec<TrackEntry>, lang: &str) -> Vec<TrackEntry> {
    let lc = lang.to_ascii_lowercase();
    let is_english = |t: &TrackEntry| -> bool {
        let l = t.language.to_ascii_lowercase();
        let n = t.name.to_ascii_lowercase();
        l.starts_with("en") || n.contains("english") || n.contains("eng")
    };
    if lc == "auto" {
        if let Some(en) = tracks.iter().find(|t| is_english(t)) {
            return vec![TrackEntry {
                number: en.number,
                track_type: en.track_type,
                codec_id: en.codec_id.clone(),
                codec_private: en.codec_private.clone(),
                name: en.name.clone(),
                language: en.language.clone(),
            }];
        }
        return Vec::new();
    }
    // Explicit ISO code: match by 2- or 3-letter prefix.
    let short = if lc.len() >= 2 { &lc[..2] } else { &lc[..] };
    tracks
        .into_iter()
        .filter(|t| {
            let l = t.language.to_ascii_lowercase();
            if l.is_empty() {
                return false;
            }
            l == lc || l.starts_with(short)
        })
        .collect()
}

