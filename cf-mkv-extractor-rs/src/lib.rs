//! AniSync MKV subtitle extractor — Cloudflare Worker (Rust/WASM).
//!
//! Port of cf-mkv-extractor/src/index.js. Same external contract:
//!   GET /?url=<RD url>&lang=auto[&shards=N&shard=X][&secret=...]
//! Returns the same JSON shape so the AniSync client can swap
//! MKV_EXTRACTOR_URL between the JS worker and this one without
//! any client changes.
//!
//! Lock-down: allowed upstream hosts hard-coded below. Optional
//! shared secret via the PROXY_SECRET worker env var.
//!
//! NOTE: First-pass port — no edge cache yet. Adding it requires
//! a clean way to clone Response bodies for cache.put; not hard,
//! just punted to a follow-up to keep the first deploy focused on
//! "does the extract work at all".

use serde::Serialize;
use serde_json::json;
use url::Url;
use worker::*;

mod ebml;
mod mkv;
mod reader;

use mkv::{extract_subtitles, ExtractOptions, TrackOutput};
use reader::RangeReader;

const ALLOWED_HOST_SUFFIXES: &[&str] = &[
    "real-debrid.com",
    "alldebrid.com",
    "debrid-link.com",
    "premiumize.me",
    "torbox.app",
    "offcloud.com",
];

#[event(fetch)]
pub async fn fetch(req: Request, env: Env, _ctx: Context) -> Result<Response> {
    console_error_panic_hook::set_once();
    // Last-resort wrapper so any unexpected error still returns a
    // structured 200 — keeps the worker out of CF's failure-rate
    // guard rail that surfaces as 503 after a few failures.
    match handle_fetch(req, env).await {
        Ok(resp) => Ok(resp),
        Err(e) => {
            let msg = e.to_string();
            console_warn!("[worker] handle_fetch error: {}", msg);
            let body = json!({
                "tracks": [],
                "extracted": false,
                "reason": classify_reason(&msg),
                "error": msg,
            });
            json_response(200, &body)
        }
    }
}

async fn handle_fetch(req: Request, env: Env) -> Result<Response> {
    let method = req.method();
    if method == Method::Options {
        return cors_preflight();
    }
    if method != Method::Get {
        return json_response(405, &json!({ "error": "method not allowed" }));
    }

    let url = req.url()?;
    let mut target: Option<String> = None;
    let mut lang: Option<String> = None;
    let mut secret_param: Option<String> = None;
    let mut shards: u32 = 1;
    let mut shard: u32 = 0;
    for (k, v) in url.query_pairs() {
        match k.as_ref() {
            "url" => target = Some(v.into_owned()),
            "lang" => lang = Some(v.to_ascii_lowercase()),
            "secret" => secret_param = Some(v.into_owned()),
            "shards" => shards = v.parse().unwrap_or(1).max(1),
            "shard" => shard = v.parse().unwrap_or(0),
            _ => {}
        }
    }

    let target = match target {
        Some(t) if !t.is_empty() => t,
        _ => return json_response(400, &json!({ "error": "missing ?url parameter" })),
    };

    // Optional shared secret. env.secret() returns Err if the
    // secret isn't set, which we treat as "no auth required".
    if let Ok(expected) = env.secret("PROXY_SECRET") {
        let expected_str = expected.to_string();
        if !expected_str.is_empty() && secret_param.as_deref() != Some(expected_str.as_str()) {
            return json_response(401, &json!({ "error": "unauthorized" }));
        }
    }

    // URL parsing via worker's re-exported Url type.
    let parsed_target = match Url::parse(&target) {
        Ok(u) => u,
        Err(_) => return json_response(400, &json!({ "error": "invalid target URL" })),
    };
    let scheme = parsed_target.scheme();
    if scheme != "https" && scheme != "http" {
        return json_response(400, &json!({ "error": "only http(s) URLs allowed" }));
    }
    let host = parsed_target.host_str().unwrap_or("").to_ascii_lowercase();
    if !host_allowed(&host) {
        return json_response(
            403,
            &json!({ "error": format!("host not allowed: {}", host) }),
        );
    }

    let mut reader = RangeReader::new(target);
    let opts = ExtractOptions { lang, shards, shard };

    let result = match extract_subtitles(&mut reader, &opts).await {
        Ok(r) => r,
        Err(e) => {
            let msg = e.to_string();
            console_warn!("[extract] failed: {}", msg);
            let reason = classify_reason(&msg);
            return json_response(
                200,
                &json!({
                    "tracks": [],
                    "extracted": false,
                    "reason": reason,
                    "error": msg,
                }),
            );
        }
    };

    let success_body = SuccessBody {
        tracks: &result.tracks,
        extracted: true,
        truncated: result.truncated,
        shard: result.shard,
        shards: result.shards,
        clusters_total: result.clusters_total,
        clusters_in_shard: result.clusters_in_shard,
        stats: Stats {
            file_size: reader.total_size.unwrap_or(0),
            track_count: result.tracks.len(),
        },
    };

    let json_text = serde_json::to_string(&success_body)
        .map_err(|e| Error::from(format!("serialize: {}", e)))?;
    let mut headers = cors_headers();
    headers.set("x-anisync-cache", "miss")?;
    let resp = Response::ok(json_text)?.with_headers(headers);
    Ok(resp)
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SuccessBody<'a> {
    tracks: &'a [TrackOutput],
    extracted: bool,
    truncated: bool,
    shard: u32,
    shards: u32,
    clusters_total: usize,
    clusters_in_shard: usize,
    stats: Stats,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Stats {
    file_size: u64,
    track_count: usize,
}

// ─── helpers ──────────────────────────────────────────────────────

fn host_allowed(host: &str) -> bool {
    ALLOWED_HOST_SUFFIXES
        .iter()
        .any(|s| host == *s || host.ends_with(&format!(".{}", s)))
}

fn cors_headers() -> Headers {
    let mut h = Headers::new();
    let _ = h.set("Access-Control-Allow-Origin", "*");
    let _ = h.set("Access-Control-Allow-Methods", "GET, OPTIONS");
    let _ = h.set("Access-Control-Allow-Headers", "Content-Type");
    let _ = h.set("Access-Control-Max-Age", "86400");
    let _ = h.set("Content-Type", "application/json; charset=utf-8");
    h
}

fn cors_preflight() -> Result<Response> {
    Ok(Response::empty()?.with_status(204).with_headers(cors_headers()))
}

fn json_response(status: u16, body: &serde_json::Value) -> Result<Response> {
    let text = body.to_string();
    Ok(Response::ok(text)?.with_status(status).with_headers(cors_headers()))
}

fn classify_reason(msg: &str) -> &'static str {
    if msg.contains("no SeekHead")
        || msg.contains("no Tracks pointer")
        || msg.contains("no index")
    {
        "indexless"
    } else if msg.contains("Network connection lost")
        || msg.contains("fetch failed")
        || msg.contains("upstream HTTP")
        || msg.contains("Memory limit")
        || msg.contains("Range ignored")
    {
        "network"
    } else {
        "parse"
    }
}

/// Used by reader.rs to decide whether a fetch error is worth
/// retrying with a different Range strategy.
pub fn is_transient_err(msg: &str) -> bool {
    msg.contains("Network connection lost")
        || msg.contains("fetch failed")
        || msg.contains("Memory limit")
        || msg.contains("upstream HTTP 5")
}
