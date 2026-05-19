//! Range-aware reader over an HTTP URL. Port of reader.js.
//!
//! Two important differences from the JS version:
//!
//! 1. `_readStreamCapped` here drains the response body stream
//!    chunk-by-chunk via `Response::stream()`, stopping when we
//!    have `length` bytes. The stream is dropped immediately
//!    after — Rust's RAII means CF's underlying ReadableStream
//!    is cancelled the moment we leave scope, releasing any
//!    buffered tail bytes. The JS version's `reader.cancel()`
//!    in `finally` doesn't always cancel synchronously, which
//!    is one of the suspects for the memory pressure we saw
//!    on the open-ended Range fallback.
//!
//! 2. Cache uses `Vec<CachedChunk>` with explicit eviction, same
//!    semantics as the JS RangeReader cache.

use crate::is_transient_err;
use futures_util::StreamExt;
use std::time::Duration;
use worker::*;

const USER_AGENT: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 \
    (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

/// Resident-cache cap. Smaller than the JS version's 40 MB because
/// the open-ended Range fallback's buffered tail eats into the
/// 128 MB cap; we'd rather keep the cache tight than chance an OOM.
const CACHE_CAP: u64 = 12 * 1024 * 1024;

struct CachedChunk {
    start: u64,
    bytes: Vec<u8>,
}

pub struct RangeReader {
    url: String,
    pub total_size: Option<u64>,
    chunks: Vec<CachedChunk>,
}

impl RangeReader {
    pub fn new(url: String) -> Self {
        Self { url, total_size: None, chunks: Vec::new() }
    }

    /// Returns the bytes for [start, start+length). Cache-hit when
    /// any single cached chunk wholly contains the range; otherwise
    /// fires a bounded fetch and caches the result.
    pub async fn read(&mut self, start: u64, length: u64) -> Result<Vec<u8>> {
        if length == 0 {
            return Ok(Vec::new());
        }
        for c in &self.chunks {
            let end = c.start + c.bytes.len() as u64;
            if c.start <= start && end >= start + length {
                let offset = (start - c.start) as usize;
                let len = length as usize;
                return Ok(c.bytes[offset..offset + len].to_vec());
            }
        }
        let bytes = self.do_fetch(start, length, false).await?;
        self.chunks.push(CachedChunk { start, bytes: bytes.clone() });
        self.evict_cache();
        Ok(bytes)
    }

    /// Reads we can't proceed without (head / tracks / cues).
    /// Three attempts:
    ///   1. Bounded at requested length (fast path).
    ///   2. Larger bounded Range (up to ~8 MB). RD's CDN seems
    ///      to treat mid-sized ranges differently from small ones
    ///      at deep offsets.
    ///   3. Open-ended Range — last resort, costs memory.
    pub async fn read_critical(&mut self, start: u64, length: u64) -> Result<Vec<u8>> {
        // Attempt 1: requested size.
        match self.read(start, length).await {
            Ok(bytes) => return Ok(bytes),
            Err(e) => {
                let msg = e.to_string();
                if !is_transient_err(&msg) {
                    return Err(e);
                }
                console_warn!(
                    "[readCritical] start={} length={}: bounded failed ({})",
                    start, length, msg
                );
            }
        }

        // Attempt 2: larger bounded Range.
        if let Some(total) = self.total_size {
            const LARGER: u64 = 8 * 1024 * 1024;
            if start < total {
                let larger_len = LARGER.min(total - start);
                if larger_len > length {
                    Delay::from(Duration::from_millis(350)).await;
                    console_log!(
                        "[readCritical] start={} length={}: trying larger bounded range of {}",
                        start, length, larger_len
                    );
                    match self.do_fetch(start, larger_len, false).await {
                        Ok(big) => {
                            console_log!(
                                "[readCritical] start={} length={}: larger bounded ok",
                                start, length
                            );
                            let result = big[..length as usize].to_vec();
                            self.chunks.push(CachedChunk { start, bytes: big });
                            self.evict_cache();
                            return Ok(result);
                        }
                        Err(e) => {
                            let msg = e.to_string();
                            if !is_transient_err(&msg) {
                                return Err(e);
                            }
                            console_warn!(
                                "[readCritical] start={} length={}: larger bounded failed ({})",
                                start, length, msg
                            );
                        }
                    }
                }
            }
        }

        // Attempt 3: open-ended Range.
        Delay::from(Duration::from_millis(700)).await;
        console_log!(
            "[readCritical] start={} length={}: trying open-ended Range",
            start, length
        );
        match self.do_fetch(start, length, true).await {
            Ok(bytes) => {
                console_log!(
                    "[readCritical] start={} length={}: open-ended Range ok",
                    start, length
                );
                self.chunks.push(CachedChunk { start, bytes: bytes.clone() });
                self.evict_cache();
                Ok(bytes)
            }
            Err(e) => {
                console_warn!(
                    "[readCritical] start={} length={}: open-ended Range also failed ({})",
                    start, length, e
                );
                Err(e)
            }
        }
    }

    /// Drops cached chunks whose start falls inside [start, end).
    /// Caller uses this to release a batch's bytes after parsing.
    pub fn evict(&mut self, start: u64, end: u64) {
        self.chunks.retain(|c| !(c.start >= start && c.start < end));
    }

    /// Ensures totalSize is known. Reads 1 byte at offset 0 if not.
    pub async fn probe_size(&mut self) -> Result<u64> {
        if let Some(t) = self.total_size {
            return Ok(t);
        }
        let _ = self.read(0, 1).await?;
        Ok(self.total_size.unwrap_or(0))
    }

    async fn do_fetch(&mut self, start: u64, length: u64, open_ended: bool) -> Result<Vec<u8>> {
        let range_header = if open_ended {
            format!("bytes={}-", start)
        } else {
            format!("bytes={}-{}", start, start + length - 1)
        };

        let mut headers = Headers::new();
        headers.set("Range", &range_header)?;
        headers.set("User-Agent", USER_AGENT)?;
        headers.set("Accept", "*/*")?;

        let mut init = RequestInit::new();
        init.with_method(Method::Get);
        init.with_headers(headers);
        init.with_redirect(RequestRedirect::Follow);

        let req = Request::new_with_init(&self.url, &init)?;
        let mut resp = Fetch::Request(req).send().await?;
        let status = resp.status_code();
        if status != 200 && status != 206 {
            return Err(Error::from(format!(
                "upstream HTTP {} on range {}",
                status, range_header
            )));
        }

        // Capture totalSize on first response that carries it.
        if self.total_size.is_none() {
            if let Ok(Some(cr)) = resp.headers().get("content-range") {
                if let Some(idx) = cr.rfind('/') {
                    if let Ok(n) = cr[idx + 1..].parse::<u64>() {
                        self.total_size = Some(n);
                    }
                }
            }
            if self.total_size.is_none() && status == 200 {
                if let Ok(Some(cl)) = resp.headers().get("content-length") {
                    if let Ok(n) = cl.parse::<u64>() {
                        self.total_size = Some(n);
                    }
                }
            }
        }

        // Range ignored — upstream returned 200 with full body.
        // Bail before we OOM on a multi-GB read.
        if status == 200 && start > 0 {
            return Err(Error::from(format!(
                "upstream returned 200 (Range ignored) for {}; cannot fetch arbitrary offset",
                range_header
            )));
        }

        // Stream the body chunk-by-chunk, hard cap at `length`.
        // For bounded Ranges length matches Content-Length so the
        // stream exhausts naturally. For open-ended Ranges we stop
        // early and drop the stream — that cancels CF's underlying
        // ReadableStream and releases the buffered tail.
        let mut stream = resp.stream()?;
        let max_bytes = length as usize;
        let mut buf: Vec<u8> = Vec::with_capacity(max_bytes);
        while buf.len() < max_bytes {
            match stream.next().await {
                Some(Ok(chunk)) => {
                    let room = max_bytes - buf.len();
                    if chunk.len() <= room {
                        buf.extend_from_slice(&chunk);
                    } else {
                        buf.extend_from_slice(&chunk[..room]);
                        break;
                    }
                }
                Some(Err(e)) => {
                    return Err(Error::from(format!("stream read failed: {}", e)));
                }
                None => break,
            }
        }
        // `stream` drops here → CF cancels the underlying reader →
        // tail bytes get released, no lingering memory pressure.
        Ok(buf)
    }

    fn evict_cache(&mut self) {
        let mut total: u64 = self.chunks.iter().map(|c| c.bytes.len() as u64).sum();
        while total > CACHE_CAP && self.chunks.len() > 2 {
            let removed = self.chunks.remove(0);
            total -= removed.bytes.len() as u64;
        }
    }
}
