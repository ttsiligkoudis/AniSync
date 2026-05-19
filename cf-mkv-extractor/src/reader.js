// Range-aware reader over an HTTP URL. Issues Range requests on demand
// and caches returned chunks so adjacent reads don't re-fetch. Targets
// the typical Matroska extraction pattern: read first ~1 MB, jump to
// the Cues offset near EOF, then read small subtitle clusters.

const DEFAULT_USER_AGENT =
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ' +
    '(KHTML, like Gecko) Chrome/120.0 Safari/537.36';

export class RangeReader {
    constructor(url) {
        this.url = url;
        this.userAgent = DEFAULT_USER_AGENT;
        /** Total file size if known (Content-Range total). */
        this.totalSize = null;
        /** Cached chunks: [{ start, bytes }] keyed by start, sorted. */
        this._chunks = [];
    }

    /**
     * Returns a Uint8Array containing bytes [start, start+length).
     * Reuses cached chunks where possible, otherwise issues a single
     * Range GET and caches the result.
     */
    async read(start, length) {
        if (length <= 0) return new Uint8Array(0);
        // Hit cached chunk wholly containing the range?
        for (const c of this._chunks) {
            const end = c.start + c.bytes.length;
            if (c.start <= start && end >= start + length) {
                return c.bytes.subarray(start - c.start, start - c.start + length);
            }
        }
        // Otherwise fetch fresh.
        const bytes = await this._fetch(start, length);
        this._chunks.push({ start, bytes });
        // Keep cache bounded. Tight — 128 MB total memory cap is
        // shared with framework overhead + the lingering bytes
        // CF's stream layer holds after we cancel an open-ended
        // Range fetch (the cues read fallback). 12 MB cache + one
        // in-flight 6 MB batch buffer + cues slab + framework
        // leaves real headroom for those stream remnants.
        let cached = 0;
        for (const c of this._chunks) cached += c.bytes.length;
        while (cached > 12 * 1024 * 1024 && this._chunks.length > 2) {
            cached -= this._chunks.shift().bytes.length;
        }
        return bytes;
    }

    async _fetch(start, length, opts) {
        opts = opts || {};
        // Open-ended ranges (`bytes=N-` with no upper bound) are
        // a workaround for RD edges that drop bounded ranges at
        // deep offsets. _readStreamCapped caps the actual read at
        // `length` regardless, so the bandwidth cost is the same as
        // a bounded range — RD just sends data through a different
        // code path on its side.
        const rangeHeader = opts.openEnded
            ? `bytes=${start}-`
            : `bytes=${start}-${start + length - 1}`;
        const headers = {
            'Range': rangeHeader,
            'User-Agent': this.userAgent,
            'Accept': '*/*',
        };
        const res = await fetch(this.url, { headers, redirect: 'follow' });
        if (!res.ok && res.status !== 206) {
            throw new Error(`upstream HTTP ${res.status} on range ${rangeHeader}`);
        }
        // Capture total size from Content-Range: "bytes start-end/total"
        if (this.totalSize === null) {
            const cr = res.headers.get('content-range') || '';
            const m = /\/(\d+)$/.exec(cr);
            if (m) this.totalSize = parseInt(m[1], 10);
            if (this.totalSize === null) {
                const cl = res.headers.get('content-length');
                if (cl && res.status === 200) this.totalSize = parseInt(cl, 10);
            }
        }
        // If the upstream returned 200 OK instead of 206, the Range
        // header was ignored — the body is the entire file starting
        // at byte 0. CF's arrayBuffer() then predicts the read would
        // overflow the worker's memory cap and emits
        // "Memory limit would be exceeded before EOF" instantly.
        // Bail with a structured error so the caller can fall back
        // instead of OOMing for the user. (RD's standard CDN does
        // honour Range; this guards against a few of their mirrors
        // that misbehave.)
        if (res.status === 200 && start > 0) {
            try { res.body.cancel && res.body.cancel(); } catch (_) {}
            throw new Error(`upstream returned 200 (Range ignored) for ${rangeHeader}; cannot fetch arbitrary offset`);
        }
        // Stream the body chunk-by-chunk with a HARD CAP at the
        // requested length. Even if the upstream Content-Length lies
        // or the server streams more bytes than asked, we stop
        // reading at `length` and cancel the stream — keeps peak
        // memory bounded to one batch regardless of upstream weirdness.
        return await this._readStreamCapped(res, length);
    }

    async _readStreamCapped(res, maxBytes) {
        const reader = res.body.getReader();
        const buf = new Uint8Array(maxBytes);
        let written = 0;
        try {
            while (written < maxBytes) {
                const { value, done } = await reader.read();
                if (done) break;
                const room = maxBytes - written;
                if (value.length <= room) {
                    buf.set(value, written);
                    written += value.length;
                } else {
                    buf.set(value.subarray(0, room), written);
                    written = maxBytes;
                    break;
                }
            }
        } finally {
            // Fire-and-forget cancel: awaiting the cancel can deadlock
            // when the upstream connection has already dropped (the
            // promise never resolves and CF surfaces it as
            // "Network connection lost" against the worker as a whole).
            // Synchronously kick it off so the upstream socket gets
            // released eventually, but don't block on it.
            try { reader.cancel(); } catch (_) {}
        }
        return written === maxBytes ? buf : buf.subarray(0, written);
    }

    /**
     * read() with a graduated retry strategy targeting RD's deep-
     * offset drop behaviour. Three attempts:
     *
     *   1. Bounded at the requested length. Fast path for small
     *      reads at shallow offsets — head, tracks, cues fits in
     *      one go on most files.
     *   2. Larger bounded Range (up to ~8 MB). RD's CDN seems to
     *      treat mid-sized ranges differently from small ones —
     *      256 KB / 512 KB / 1 MB drops are common at deep
     *      offsets, but 8 MB ranges often go through. Possibly
     *      because they look more like normal browser streaming
     *      reads. We cache the larger chunk so subsequent reads in
     *      the same region hit cache.
     *   3. Open-ended Range ("bytes=N-") as last resort. RD streams
     *      "rest of file"; CF's stream layer holds the buffered
     *      tail in memory until GC, which can blow the 128 MB cap
     *      on big files. Only used when bounded approaches both
     *      fail, because the memory cost is real.
     *
     * Previous attempt (smaller-and-smaller splits 1/2/4) didn't
     * help: real-world logs show 256 KB drops at the same offsets
     * 1 MB drops. The size axis isn't where the recovery lives;
     * the LARGER bounded attempt + open-ended fallback is.
     */
    async readCritical(start, length) {
        const isTransient = (msg) =>
            msg.includes('Network connection lost') ||
            msg.includes('fetch failed') ||
            msg.includes('Memory limit') ||
            msg.includes('upstream HTTP 5');

        // ── Attempt 1: bounded at requested length ───────────────
        try {
            return await this.read(start, length);
        } catch (e) {
            const msg = (e && e.message) || String(e);
            if (!isTransient(msg)) throw e;
            console.warn(`[readCritical] start=${start} length=${length}: bounded request failed (${msg})`);
        }

        // ── Attempt 2: larger bounded Range ──────────────────────
        if (this.totalSize && start < this.totalSize) {
            const LARGER_SIZE = 8 * 1024 * 1024;
            const largerLen = Math.min(LARGER_SIZE, this.totalSize - start);
            if (largerLen > length) {
                await new Promise(r => setTimeout(r, 350));
                try {
                    console.log(`[readCritical] start=${start} length=${length}: trying larger bounded range of ${largerLen}`);
                    const big = await this._fetch(start, largerLen);
                    this._chunks.push({ start, bytes: big });
                    console.log(`[readCritical] start=${start} length=${length}: larger bounded ok`);
                    return big.subarray(0, length);
                } catch (e) {
                    const msg = (e && e.message) || String(e);
                    if (!isTransient(msg)) throw e;
                    console.warn(`[readCritical] start=${start} length=${length}: larger bounded failed (${msg})`);
                }
            }
        }

        // ── Attempt 3: open-ended Range (memory-risky) ───────────
        await new Promise(r => setTimeout(r, 700));
        try {
            console.log(`[readCritical] start=${start} length=${length}: trying open-ended Range`);
            const bytes = await this._fetch(start, length, { openEnded: true });
            this._chunks.push({ start, bytes });
            console.log(`[readCritical] start=${start} length=${length}: open-ended Range ok`);
            return bytes;
        } catch (e) {
            console.warn(`[readCritical] start=${start} length=${length}: open-ended Range also failed (${(e && e.message) || e})`);
            throw e;
        }
    }

    /**
     * Drops any cached chunk that starts inside [start, end). Used
     * by the extractor to release a batch's bytes the moment its
     * clusters have been parsed — keeps peak memory well below
     * Cloudflare's 128 MB Worker cap during long extractions.
     */
    evict(start, end) {
        for (let i = this._chunks.length - 1; i >= 0; i--) {
            const c = this._chunks[i];
            if (c.start >= start && c.start < end) {
                this._chunks.splice(i, 1);
            }
        }
    }

    /**
     * HEADs the file (well, GET with bytes=0-0) just to populate
     * totalSize. Useful at startup so later "read from end" math works.
     */
    async probeSize() {
        if (this.totalSize !== null) return this.totalSize;
        await this.read(0, 1);
        return this.totalSize;
    }
}
