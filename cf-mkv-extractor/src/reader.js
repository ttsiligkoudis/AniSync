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
        // Keep cache bounded — total file might be 700+ MB and we
        // only ever need ~30 MB of subtitle data. Drop oldest if we
        // ever exceed 80 MB cached.
        let cached = 0;
        for (const c of this._chunks) cached += c.bytes.length;
        while (cached > 80 * 1024 * 1024 && this._chunks.length > 2) {
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
        const buf = new Uint8Array(await res.arrayBuffer());
        return buf;
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
