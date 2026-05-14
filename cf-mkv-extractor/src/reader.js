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
        // overhead. Hold ~16 MB resident at most so even an
        // in-flight 8 MB fetch + leftover cache + worker overhead
        // stays well under the cliff.
        let cached = 0;
        for (const c of this._chunks) cached += c.bytes.length;
        while (cached > 16 * 1024 * 1024 && this._chunks.length > 2) {
            cached -= this._chunks.shift().bytes.length;
        }
        return bytes;
    }

    async _fetch(start, length) {
        const headers = {
            'Range': `bytes=${start}-${start + length - 1}`,
            'User-Agent': this.userAgent,
            'Accept': '*/*',
        };
        const res = await fetch(this.url, { headers, redirect: 'follow' });
        if (!res.ok && res.status !== 206) {
            throw new Error(`upstream HTTP ${res.status} on range ${start}-${start + length - 1}`);
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
            throw new Error(`upstream returned 200 (Range ignored) for range ${start}-${start + length - 1}; cannot fetch arbitrary offset`);
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
            // Release the upstream connection promptly so CF doesn't
            // hold the body buffer alive waiting for us to finish.
            try { await reader.cancel(); } catch (_) {}
        }
        return written === maxBytes ? buf : buf.subarray(0, written);
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
