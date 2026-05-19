# cf-mkv-extractor-rs

Rust/WebAssembly port of `cf-mkv-extractor/` for testing against the
JS implementation. Same external API — point AniSync's
`MKV_EXTRACTOR_URL` config at this worker's URL to use it instead.

## Why a separate worker?

Cloudflare Workers' per-invocation caps (10 ms CPU, 128 MB memory on
Free) hit hard with the JS implementation on big anime files. Rust
compiled to WASM uses both more efficiently:

- **CPU.** The cluster-walk hot path is tens of thousands of EBML
  block headers parsed in JS — V8 overhead dominates. Rust does the
  same work without per-call allocations.
- **Memory.** No V8 heap, no GC lag. RAII means buffers drop
  predictably; the `Response::stream()` drop on early-exit cancels
  the underlying ReadableStream synchronously, which is the
  suspected fix for the open-ended-Range tail-buffer issue.

## Deploy

One-time setup (Rust + worker-build):

```sh
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh
cargo install worker-build
rustup target add wasm32-unknown-unknown
```

Then from this directory:

```sh
npx wrangler deploy
```

`wrangler.toml`'s `[build]` block runs `worker-build --release`
which compiles the crate to WASM and emits a JS shim that the CF
runtime loads. First build takes a couple of minutes; subsequent
builds are seconds because Cargo caches.

## Wire it up

Set the same env vars on AniSync as for the JS worker:

```
MkvExtractor:Url    = https://anisync-mkv-extractor-rs.<your-subdomain>.workers.dev
MkvExtractor:Secret = (optional, must match `wrangler secret put PROXY_SECRET`)
```

The JSON shape matches the JS worker exactly (`tracks`, `extracted`,
`truncated`, `shard`, `shards`, `clustersTotal`, `clustersInShard`,
`stats`, `reason`, `error`), so the AniSync client doesn't need any
changes to switch between them.

## What's not here yet

- **Edge cache.** The JS worker caches successful extractions for
  2 hours at CF's edge. Punted to a follow-up — adding it requires
  a clean Response body clone for `cache.put`, and the first build
  is focused on validating the extract path.

## Tuning

Same constants as the JS worker in `src/mkv.rs`:

| Const                          | Value      | Notes |
|--------------------------------|-----------:|-------|
| `HEAD_BYTES`                   | 256 KB     | EBML header + Segment + SeekHead |
| `TRACKS_FETCH`                 | 256 KB     | Initial Tracks read |
| `CUES_FETCH`                   | 256 KB     | Initial cues read; top-up if bigger |
| `CLUSTER_FETCH`                | 4 MB       | Per-cluster read |
| `MAX_BATCH_SIZE`               | 6 MB       | Per coalesced batch |
| `MAX_TOTAL_FETCH`              | 50 MB      | Total RD bandwidth per invocation |
| `MAX_CLUSTER_BATCHES`          | 30         | Subrequest budget guard |
| `SINGLE_SHOT_CLUSTER_CEILING`  | 80         | Single-shot bails above this → client shards |

Rust's lower per-block CPU lets us run a more permissive cluster
ceiling (80 vs the JS worker's 20). If real-world tests hit CPU
on bigger files, drop it.
