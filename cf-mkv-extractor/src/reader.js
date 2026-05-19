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
        // Keep cache bounded. Cloudflare Workers' 128 MB memory cap
        // is shared with the in-flight Response body + framework
        // overhead. 40 MB resident leaves headroom for one in-flight
        // 24 MB stream-cap buffer + the head/tracks/cues slabs +
        // framework. Single-track extraction (lang=auto) keeps the
        // cuesByTrack Map well under 1 MB, so we don't have to budget
        // aggressively for accumulated state.
        let cached = 0;
        for (const c of this._chunks) cached += c.bytes.length;
        while (cached > 40 * 1024 * 1024 && this._chunks.length > 2) {
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
     * read() with a divide-and-conquer retry strategy. Use for
     * fetches we can't proceed without (head / tracks / cues). When
     * RD's edges drop a large Range mid-stream — "Network connection
     * lost" being the canonical CF surface error — the most reliable
     * recovery is to ask for the same bytes in smaller pieces. Some
     * RD edges accept a 512 KB Range after dropping a 4 MB one on
     * the same offset.
     *
     * Attempts (with cumulative subrequest cost): 1 (full) → 2
     * (halves) → 4 (quarters). Capped at 3 attempts and at a 64 KB
     * floor on chunk size; past that the per-fetch overhead
     * dominates and further splitting hurts more than it helps.
     * Worst case 7 subrequests per critical read, leaving plenty of
     * headroom under CF Free's 50-subrequest invocation cap for the
     * 20-ish cluster-batch fetches in the main loop.
     *
     * Cluster batches stay on the no-retry path: they're individually
     * skippable (the batch loop `continue`s past a failed batch),
     * so spending the retry budget on them would crowd out the
     * critical-read budget where retries actually matter.
     */
    async readCritical(start, length) {
        const ATTEMPTS = [
            { splits: 1, backoff: 0 },
            { splits: 2, backoff: 350 },
            { splits: 4, backoff: 1500 },
        ];
        let lastError = null;
        for (let attempt = 0; attempt < ATTEMPTS.length; attempt++) {
            const a = ATTEMPTS[attempt];
            if (a.backoff > 0) {
                await new Promise(r => setTimeout(r, a.backoff));
            }
            const chunkSize = Math.ceil(length / a.splits);
            if (chunkSize < 64 * 1024 && a.splits > 1) {
                // Skip this attempt — too small to help. Try the
                // next one, or fall through to the final throw.
                continue;
            }
            try {
                const result = new Uint8Array(length);
                let written = 0;
                for (let i = 0; i < a.splits; i++) {
                    const chunkStart = start + i * chunkSize;
                    const remaining = length - written;
                    const thisLen = Math.min(chunkSize, remaining);
                    const chunk = await this.read(chunkStart, thisLen);
                    result.set(chunk, written);
                    written += chunk.length;
                    if (chunk.length < thisLen) {
                        // Server sent less than asked — return
                        // partial and let the caller decide.
                        console.log(`[readCritical] start=${start} length=${length} splits=${a.splits}: short read, returning ${written} bytes`);
                        return result.subarray(0, written);
                    }
                }
                if (attempt > 0) {
                    console.log(`[readCritical] start=${start} length=${length} splits=${a.splits}: ok after ${attempt} prior failure(s)`);
                }
                return result;
            } catch (e) {
                lastError = e;
                const msg = (e && e.message) || String(e);
                const transient =
                    msg.includes('Network connection lost') ||
                    msg.includes('fetch failed') ||
                    msg.includes('Memory limit') ||
                    msg.includes('upstream HTTP 5');
                console.warn(`[readCritical] start=${start} length=${length} splits=${a.splits}: failed (${msg})`);
                if (!transient) throw e;
                // Try next attempt with more splits + longer backoff.
            }
        }

        // All bounded-Range attempts failed. As a final fallback,
        // try an open-ended Range request at the same start offset.
        // Real-world finding (worker logs on big SubsPlease files,
        // 2026-05): RD's CDN edges that serve CF worker IPs
        // sometimes accept the TCP connection on bounded Ranges
        // anchored at deep offsets but then drop the body
        // mid-stream — "Network connection lost" — while
        // accepting open-ended Ranges starting at the same offset.
        // _readStreamCapped still caps the read locally at
        // `length` so we don't actually transfer the rest of the
        // file (RD's TCP send buffer might push a few hundred KB
        // extra before our cancel takes effect, but no more).
        try {
            console.log(`[readCritical] start=${start} length=${length}: bounded attempts exhausted; trying open-ended Range`);
            const bytes = await this._fetch(start, length, { openEnded: true });
            this._chunks.push({ start, bytes });
            console.log(`[readCritical] start=${start} length=${length}: open-ended Range ok`);
            return bytes;
        } catch (e) {
            console.warn(`[readCritical] start=${start} length=${length}: open-ended Range also failed (${(e && e.message) || e})`);
            throw lastError || e;
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
