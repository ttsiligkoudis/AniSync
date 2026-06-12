// Web-head media-player interop. The shared Watch page resolves IMediaPlayer to
// Html5MediaPlayer on the browser; that C# service drives playback through these
// functions so resume, progress (auto-track) and ended (auto-play-next) work the
// same way the native LibVLCSharp head does — keeping all the resume/scrobble
// policy in the shared C# layer.
//
// Stage-3b: the bare <video> is replaced by the ArtPlayer engine — a verbatim
// port of the `new Artplayer({...})` setup in Views/Meta/Watch.cshtml (theme
// #7c3aed, ±10s rewind/forward SVG controls, settings menu, playbackRate,
// fullscreen + fullscreenWeb, subtitleOffset, autoOrientation, airplay, hotkey,
// pip:false, the capture-phase touch guard, AniSkip `highlight` ticks). The public
// contract (play / seek / setSubtitle / stop + the OnProgress / OnEnded /
// OnStreamEvent callbacks) is unchanged — internally it drives ArtPlayer's
// art.video instead of a raw element.
//
// The embedded-MKV subtitle pipeline (matroska-subtitles streaming extractor +
// jassub bootstrap, ASS→VTT, English auto-promote, embedded→OpenSubtitles
// handover, the CF CORS proxy, the mobile opt-in + the embedded-status pill +
// subtitle chip) is ported here too — it's a browser-only concern (Range fetch,
// WebAssembly, blob: URLs) and ArtPlayer is web-only, so it can't live in shared
// C#. setContext() seeds the per-episode metadata the pipeline needs; play()
// builds the player + kicks the extraction.
window.anisyncWatch = (function () {
    'use strict';

    // ── Per-episode context (seeded by Watch.razor via setContext) ────────────
    // The shared C# layer can't reach the browser-only embedded-subtitle pipeline,
    // so the page hands it the static metadata it needs once per source pick:
    // episode identity (for the /episode-subtitles + /resolve-stream fetches), the
    // API origin + config header (so the fetch authenticates the same way
    // AniSyncApi does), the AniSkip skipTimes (progress-bar highlight + paint), and
    // the optional CF CORS proxy (unset → embedded extraction skips CORS-blocked
    // debrid hosts silently, matching the original's CORS_PROXY_URL-unset default).
    var ctx = {
        id: '', season: null, episode: 0, type: '', filename: '',
        title: '', apiBase: '', configHeader: 'X-AniSync-Config', config: '',
        clientHeader: 'X-AniSync-Client', clientValue: 'anisync-app',
        corsProxyUrl: '', corsProxySecret: '',
        skipTimes: null,
    };

    function setContext(next) {
        if (!next) return;
        ctx = Object.assign({}, ctx, next);
        // Normalise the API base to a trailing-slash-free origin so the URL
        // builders below can `apiBase + '/api/v1/...'` uniformly.
        if (ctx.apiBase) ctx.apiBase = String(ctx.apiBase).replace(/\/+$/, '');
    }

    // Build a same-credential request to the AniSync API. Mirrors AniSyncApi's
    // headers: the config credential (X-AniSync-Config) authenticates /me/* calls,
    // the client tag (X-AniSync-Client) exempts the rate limiter.
    function apiUrl(path) {
        return (ctx.apiBase || '') + path;
    }
    function apiFetch(path, init) {
        init = init || {};
        var headers = Object.assign({ 'Accept': 'application/json' }, init.headers || {});
        if (ctx.config) headers[ctx.configHeader] = ctx.config;
        if (ctx.clientValue) headers[ctx.clientHeader] = ctx.clientValue;
        init.headers = headers;
        if (init.credentials === undefined) init.credentials = 'same-origin';
        return fetch(apiUrl(path), init);
    }

    // ── Live ArtPlayer instance + listener state ──────────────────────────────
    let art = null;
    let dotnetRef = null; // DotNetObjectReference exposing OnProgress / OnEnded / OnStreamEvent
    let watchdog = null;
    let watchdogMs = 25000;
    let bufferStallTimestamps = [];
    let bufferHintShown = false;

    // Live JASSUB renderer for SSA/ASS tracks (kept across selector changes).
    let jassubInstance = null;
    // Embedded MKV subtitle tracks discovered by streaming matroska-subtitles.
    let embeddedTracks = [];
    // Cancellation for the in-flight MKV extraction (abort on source switch).
    let embeddedExtractAbort = null;
    // OS subtitle list for the current source (passed in via play options).
    let osSubtitles = [];
    // Which subtitle entry is currently active — drives the menu's default marker
    // so a mid-playback auto-promote shows up checked.
    let currentSubKind = 'none';   // 'none' | 'vtt' | 'ass'
    let currentSubKey = null;      // url for vtt, label string for ass
    let buildSubtitleSelector = null; // set per source pick (closure over osSubtitles)
    let _handoverHandler = null;

    // ── iOS / network heuristics (mirrors the original) ───────────────────────
    function isIosSafari() {
        var ua = '';
        try { ua = navigator.userAgent || ''; } catch (e) { return false; }
        if (/iPad|iPhone|iPod/.test(ua)) return true;
        if (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1) return true;
        return false;
    }
    var IS_IOS_SAFARI = isIosSafari();
    var STREAM_WATCHDOG_MS = IS_IOS_SAFARI ? 8000 : 25000;

    // ── Watchdog + stream events → .NET ───────────────────────────────────────
    function cancelWatchdog() {
        if (watchdog) { clearTimeout(watchdog); watchdog = null; }
    }

    // Surface a stream event back to .NET. kind: 'error' | 'timeout' | 'playing'
    // | 'stall'. reason (for 'error'): 'network' | 'decode'.
    function emit(kind, reason) {
        if (dotnetRef) {
            dotnetRef.invokeMethodAsync('OnStreamEvent', kind, reason || null)
                .catch(function () { /* circuit gone */ });
        }
    }

    function startWatchdog() {
        cancelWatchdog();
        watchdog = setTimeout(function () {
            watchdog = null;
            var v = art && art.video;
            if (!v) return;
            // HAVE_CURRENT_DATA (2)+ means decoding started — leave it alone.
            if (v.readyState >= 2 || v.currentTime > 0) return;
            emit('timeout');
        }, watchdogMs);
    }

    // Buffer-underrun heuristic mirrored from the original: surface a hint once
    // 3 stalls cluster inside a 30s window, then stay quiet for the session.
    function recordBufferStall(kind) {
        var now = Date.now();
        bufferStallTimestamps.push(now);
        while (bufferStallTimestamps.length && now - bufferStallTimestamps[0] > 30000) {
            bufferStallTimestamps.shift();
        }
        try {
            var v = art && art.video;
            var at = v && typeof v.currentTime === 'number' ? v.currentTime.toFixed(1) + 's' : '(unknown)';
            console.log('[AniSync] buffer ' + kind + ' at', at, 'recent stalls:', bufferStallTimestamps.length);
        } catch (_) { }
        if (bufferStallTimestamps.length >= 3 && !bufferHintShown) {
            bufferHintShown = true;
            // ArtPlayer's transient notice overlay; also forward a 'stall' so the
            // page can mirror the hint (Watch.razor surfaces an inline toast).
            try { if (art && art.notice) art.notice.show = 'Playback laggy? Use the external-player button next to the source for a smoother stream.'; }
            catch (_) { }
            emit('stall');
        }
    }

    // ── Player teardown ───────────────────────────────────────────────────────
    function destroyPlayer() {
        // ArtPlayer.destroy(true) detaches the player, removes its DOM, and
        // unbinds every listener it registered. Without `true` it only pauses +
        // retains its DOM — we'd accumulate stale instances per source pick.
        if (art) { try { art.destroy(true); } catch (_) { } art = null; }
        cancelWatchdog();
        tearDownJassub();
        if (embeddedExtractAbort) {
            try { embeddedExtractAbort.abort(); } catch (_) { }
            embeddedExtractAbort = null;
        }
        embeddedTracks = [];
        bufferStallTimestamps = [];
        bufferHintShown = false;
        // art.destroy(true) already unbound the timeupdate handover listener; just
        // drop our reference so the next source pick rearms cleanly.
        _handoverHandler = null;
    }

    function tearDownJassub() {
        if (jassubInstance) {
            try { jassubInstance.destroy(); } catch (_) { }
            jassubInstance = null;
        }
    }

    function el(tag, attrs, text) {
        var n = document.createElement(tag);
        if (attrs) Object.keys(attrs).forEach(function (k) {
            if (k === 'class') n.className = attrs[k];
            else n.setAttribute(k, attrs[k]);
        });
        if (text != null) n.textContent = text;
        return n;
    }

    // ── Embedded-status pill + subtitle chip (toolbar) ────────────────────────
    function embedStatusEl() { return document.getElementById('watchEmbeddedStatus'); }
    function subsProviderEl() { return document.getElementById('watchSubsProvider'); }

    // Surface the embedded-subtitle pipeline state. `kind` drives the pill colour:
    // loading / success / none / error.
    function setEmbedStatus(text, kind) {
        var pill = embedStatusEl();
        if (!pill) return;
        if (!text) {
            pill.hidden = true;
            pill.textContent = '';
            pill.removeAttribute('data-kind');
            return;
        }
        pill.hidden = false;
        pill.textContent = text;
        pill.setAttribute('data-kind', kind || '');
    }

    // Render a "Subs · OS: X" chip after the subtitle fetch resolves. data-kind
    // drives the colour so an empty result reads distinct from a populated one.
    function renderSubsProviderChip(counts) {
        var chip = subsProviderEl();
        if (!chip) return;
        var os = (counts && counts.opensubtitles) || 0;
        if (os === 0) {
            chip.hidden = false;
            chip.textContent = 'No remote subtitles';
            chip.setAttribute('data-kind', 'none');
            return;
        }
        chip.hidden = false;
        chip.textContent = 'Subs · OS: ' + os;
        chip.setAttribute('data-kind', 'success');
    }

    // ── Subtitle dispatcher ───────────────────────────────────────────────────
    // Three kinds: 'none' (clear), 'vtt' (native <track>), 'ass' (SSA→VTT). The
    // browser's native <track mode=showing> renders cues because it honours WebVTT
    // cue settings (line / position / align) — ArtPlayer's overlay ignores them and
    // libass/JASSUB can't run on tainted (cross-origin, no-CORS) video.
    function applySubtitle(item) {
        tearDownJassub();
        if (!art) return;
        art.subtitle.show = false;
        if (item.kind === 'none') {
            setActiveSubtitleTrack(null, null, null);
            currentSubKind = 'none';
            currentSubKey = null;
            return;
        }
        if (item.kind === 'vtt') {
            setActiveSubtitleTrack(item.url, item.html, item.lang);
            currentSubKind = 'vtt';
            currentSubKey = item.url;
            return;
        }
        if (item.kind === 'ass') {
            currentSubKind = 'ass';
            currentSubKey = item.html || (item.label || 'Embedded') + ' SSA';
            renderAssAsVtt(item);
            return;
        }
    }

    // Single source of truth for the active subtitle track — appends a
    // <track data-active-subtitle mode=showing> child onto art.video. Skipped for
    // 'none' (just clears). Doubles as the cast track (Chromecast picks up <track>
    // children automatically).
    function setActiveSubtitleTrack(url, label, lang) {
        if (!art || !art.video) return;
        var video = art.video;
        var track = video.querySelector('track[data-active-subtitle]');
        if (!url) {
            if (track) track.parentNode.removeChild(track);
            return;
        }
        if (!track) {
            track = document.createElement('track');
            track.setAttribute('data-active-subtitle', '1');
            track.kind = 'subtitles';
            track.default = true;
            video.appendChild(track);
        }
        // Absolutise URLs — cast receivers can't resolve path-relative refs against
        // the sender tab's location.
        try { track.src = new URL(url, location.href).href; }
        catch (_) { track.src = url; }
        if (lang) track.srclang = lang;
        if (label) track.label = label;
        try { if (track.track) track.track.mode = 'showing'; }
        catch (_) { /* IE-style track shape — ignore */ }
    }

    // Auto-handover: when the embedded SSA track ends before the episode does (the
    // extractor's early-exit cuts off at the credit roll), seamlessly switch to a
    // same-language OpenSubtitles VTT so post-credit dialogue still shows.
    function setupEmbeddedToOsHandover(embeddedTrack, osList, onSwitched) {
        if (!art || !embeddedTrack) {
            console.info('[AniSync] handover skipped: no art / no embeddedTrack');
            return;
        }
        if (_handoverHandler) {
            art.off('video:timeupdate', _handoverHandler);
            _handoverHandler = null;
        }

        var lastCueEndMs = 0;
        var cues = (embeddedTrack.cues) || [];
        for (var i = 0; i < cues.length; i++) {
            var endMs = (cues[i].time || 0) + (cues[i].duration || 0);
            if (endMs > lastCueEndMs) lastCueEndMs = endMs;
        }
        if (lastCueEndMs === 0) {
            console.info('[AniSync] handover skipped: embedded track has no cues');
            return;
        }

        var embLang2 = (embeddedTrack.language || '').toLowerCase().slice(0, 2);
        if (!embLang2 || embLang2 === 'un') {
            var name = (embeddedTrack.name || '').toLowerCase();
            if (/\benglish\b/.test(name) || /\beng\b/.test(name)) {
                embLang2 = 'en';
            } else {
                console.info('[AniSync] handover skipped: embedded language unknown (tag="' +
                    (embeddedTrack.language || '') + '", name="' + (embeddedTrack.name || '') + '")');
                return;
            }
        }

        var matchingOs = null;
        var list = osList || [];
        for (var j = 0; j < list.length; j++) {
            var ol = (list[j].lang || '').toLowerCase().slice(0, 2);
            if (ol === embLang2) { matchingOs = list[j]; break; }
        }
        if (!matchingOs) {
            console.info('[AniSync] handover skipped: no matching OS track for lang=' + embLang2);
            return;
        }

        console.info('[AniSync] handover armed: embedded ends at', Math.round(lastCueEndMs / 1000) + 's,',
            'lang=' + embLang2 + ',', 'OS target=' + (matchingOs.label || matchingOs.lang || matchingOs.url));

        var handoverDone = false;
        _handoverHandler = function () {
            if (handoverDone) return;
            if (currentSubKind !== 'ass') {
                art.off('video:timeupdate', _handoverHandler);
                _handoverHandler = null;
                return;
            }
            var v = art && art.video;
            if (!v) return;
            if (!isFinite(v.duration) || v.duration <= 0) return;
            var videoDurationMs = v.duration * 1000;
            if (videoDurationMs - lastCueEndMs < 5000) {
                art.off('video:timeupdate', _handoverHandler);
                _handoverHandler = null;
                return;
            }
            var nowMs = (v.currentTime || 0) * 1000;
            if (nowMs >= lastCueEndMs - 500) {
                handoverDone = true;
                try {
                    console.log('[AniSync] embedded → OS handover at', Math.round(nowMs / 1000) + 's',
                        '(embedded ended at', Math.round(lastCueEndMs / 1000) + 's of', Math.round(v.duration) + 's)');
                } catch (_) { }
                applySubtitle({
                    kind: 'vtt',
                    url: matchingOs.url,
                    html: matchingOs.label || matchingOs.lang || 'OpenSubtitles',
                });
                if (typeof onSwitched === 'function') { try { onSwitched(); } catch (_) { } }
                art.off('video:timeupdate', _handoverHandler);
                _handoverHandler = null;
            }
        };
        art.on('video:timeupdate', _handoverHandler);
    }

    // Rebuild the subtitles selector inside ArtPlayer's settings menu — called
    // after MKV extraction surfaces new embedded tracks. ArtPlayer's settings API
    // has shifted shape across 5.x; try the documented paths in order.
    function refreshSubtitleSelector(rebuildFn) {
        if (!art || !art.setting) {
            console.warn('refreshSubtitleSelector: no art.setting');
            return;
        }
        var newOptions;
        try { newOptions = rebuildFn(); }
        catch (e) { console.warn('rebuildFn threw:', e); return; }

        var newEntry = {
            name: 'subtitles',
            html: 'Subtitles',
            tooltip: 'Subtitle track',
            selector: newOptions,
            onSelect: function (item) { applySubtitle(item); return item.html; },
        };
        try {
            if (typeof art.setting.update === 'function') { art.setting.update(newEntry); return; }
        } catch (e) { console.warn('setting.update failed:', e); }
        try {
            if (typeof art.setting.remove === 'function' && typeof art.setting.add === 'function') {
                art.setting.remove('subtitles');
                art.setting.add(newEntry);
                return;
            }
        } catch (e) { console.warn('setting.remove+add failed:', e); }
        console.warn('No working ArtPlayer settings API found; selector not refreshed');
    }

    // ── CORS proxy + host classification ──────────────────────────────────────
    function viaCorsProxy(url) {
        if (!ctx.corsProxyUrl || !url) return null;
        var u = ctx.corsProxyUrl.replace(/\/$/, '') + '/?url=' + encodeURIComponent(url);
        if (ctx.corsProxySecret) u += '&secret=' + encodeURIComponent(ctx.corsProxySecret);
        return u;
    }
    var CORS_BLOCKED_HOST_RE = /(?:^|\.)(?:real-debrid\.com|alldebrid\.com|debrid-link\.com|premiumize\.me|torbox\.app|offcloud\.com|strem\.fun)$/i;
    function isCorsBlockedHost(url) {
        try { return CORS_BLOCKED_HOST_RE.test(new URL(url, window.location.href).hostname); }
        catch (_) { return false; }
    }
    var CDN_HOST_RE = /(?:^|\.)(?:real-debrid\.com|alldebrid\.com|debrid-link\.com|premiumize\.me|torbox\.app|offcloud\.com)$/i;

    // ── Network classification (cellular / data-saver / mobile-unknown) ───────
    var EMBED_MOBILE_OPTIN_KEY = 'anisync.embed.tryOnMobile';
    function classifyNetwork() {
        try {
            var ua = navigator.userAgent || '';
            var isMobile = /Mobi|Mobile|Android|iPhone|iPad|iPod/i.test(ua);
            var c = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
            if (c) {
                if (c.saveData) return 'data-saver';
                if (c.type === 'cellular') return 'cellular';
                var et = c.effectiveType;
                if (et === 'slow-2g' || et === '2g' || et === '3g') return 'slow-connection';
                if (typeof c.downlink === 'number' && c.downlink > 0 && c.downlink < 1.5) return 'low-bandwidth';
                if (isMobile && c.type !== 'wifi' && c.type !== 'ethernet') return 'mobile-unknown-network';
            } else if (isMobile) {
                return 'mobile-unknown-network';
            }
        } catch (_) { }
        return null;
    }
    var OPTIN_OVERRIDES = { 'cellular': 1, 'mobile-unknown-network': 1 };
    function networkSkipReason() {
        var reason = classifyNetwork();
        if (reason && OPTIN_OVERRIDES[reason]) {
            try { if (localStorage.getItem(EMBED_MOBILE_OPTIN_KEY) === '1') return null; }
            catch (_) { }
        }
        return reason;
    }

    // Resolve a Torrentio resolver URL to the post-redirect debrid CDN URL. Two
    // paths raced: the server-side /resolve-stream follower (retried with backoff)
    // and the Resource Timing API. Reject (not fall back to Torrentio) on total
    // failure so the caller can skip extraction quietly.
    function resolveStreamUrl(originalUrl, signal) {
        return new Promise(function (resolve, reject) {
            var TIMEOUT_MS = 25000;
            var MAX_ATTEMPTS = 5;
            var done = false, observer = null, attempts = 0, retryTimer = null;

            function finish(u, viaReject) {
                if (done) return;
                done = true;
                if (observer) { try { observer.disconnect(); } catch (_) { } }
                if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; }
                if (viaReject) reject(u); else resolve(u);
            }
            function tryServerResolve() {
                attempts++;
                apiFetch('/api/v1/resolve-stream?url=' + encodeURIComponent(originalUrl), { signal: signal })
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (data) {
                        if (done) return;
                        var u = data && data.resolvedUrl;
                        if (u) {
                            try { if (CDN_HOST_RE.test(new URL(u).hostname)) { finish(u); return; } }
                            catch (_) { }
                        }
                        scheduleRetry();
                    })
                    .catch(function () { scheduleRetry(); });
            }
            function scheduleRetry() {
                if (done) return;
                if (attempts >= MAX_ATTEMPTS) { finish(new Error('resolve failed after ' + MAX_ATTEMPTS + ' attempts'), true); return; }
                retryTimer = setTimeout(tryServerResolve, 1000 * attempts);
            }
            function findResolved() {
                try {
                    var entries = performance.getEntriesByType('resource');
                    for (var i = entries.length - 1; i >= 0; i--) {
                        try { if (CDN_HOST_RE.test(new URL(entries[i].name).hostname)) return entries[i].name; }
                        catch (_) { }
                    }
                } catch (_) { }
                return null;
            }
            var hit = findResolved();
            if (hit) { finish(hit); return; }
            try {
                observer = new PerformanceObserver(function () { var h = findResolved(); if (h) finish(h); });
                observer.observe({ entryTypes: ['resource'] });
            } catch (_) { }
            tryServerResolve();
            if (signal) signal.addEventListener('abort', function () { finish(new Error('aborted'), true); });
            setTimeout(function () { finish(new Error('resolve timeout'), true); }, TIMEOUT_MS);
        });
    }

    // Hands a stream URL to /resolve-stream and returns the post-redirect URL
    // (debrid CDN, not IP-bound). Falls back to the original on any failure.
    function resolvePlaybackUrl(originalUrl) {
        if (!originalUrl) return Promise.resolve(originalUrl);
        return apiFetch('/api/v1/resolve-stream?url=' + encodeURIComponent(originalUrl))
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) { return (data && data.resolvedUrl) || originalUrl; })
            .catch(function () { return originalUrl; });
    }

    // ── matroska-subtitles + jassub ESM bootstrap (CDN, parallel, timed-out) ──
    // Both ship ESM/Node-only on npm; esm.sh auto-bundles for the browser. Each
    // import has a fallback CDN chain with a 6s timeout so a hung CDN falls through.
    var _libsReady = null;
    function loadSubtitleLibs() {
        if (_libsReady) return _libsReady;
        function timeoutPromise(ms, message) {
            return new Promise(function (_, reject) { setTimeout(function () { reject(new Error(message)); }, ms); });
        }
        // validate (optional): given the loaded module, return true if it's usable. Lets us fall through to
        // the next CDN when a build imports fine but is broken at runtime (e.g. an esm.sh bundle that
        // mistranspiles a class's base so `new` throws "Class constructor … cannot be invoked without 'new'").
        async function tryImport(specifiers, timeoutMs, validate) {
            timeoutMs = timeoutMs || 6000;
            for (var k = 0; k < specifiers.length; k++) {
                var spec = specifiers[k];
                try {
                    var mod = await Promise.race([import(spec), timeoutPromise(timeoutMs, 'import timeout: ' + spec)]);
                    if (!validate || validate(mod)) return mod;
                    console.warn('module loaded but failed validation, trying next CDN:', spec);
                } catch (e) { console.warn('module load attempt failed:', spec, e && e.message); }
            }
            return null;
        }
        _libsReady = (async function () {
            var status = { matroska: false, jassub: false };
            var pair = await Promise.all([
                tryImport([
                    'https://esm.sh/matroska-subtitles?bundle&target=esnext',
                    'https://esm.sh/matroska-subtitles?target=esnext',
                    'https://esm.run/matroska-subtitles',
                    'https://cdn.skypack.dev/matroska-subtitles',
                ], 6000, function (m) {
                    // The SubtitleParser must actually be constructible — some bundles transpile its
                    // Writable base inconsistently, so `new Parser()` throws. Test-construct a throwaway and
                    // reject this build if it can't, so the next CDN is tried.
                    var P = m && (m.SubtitleParser || (m.default && m.default.SubtitleParser) || m.default);
                    if (typeof P !== 'function') return false;
                    try { new P(); return true; }
                    catch (e) { console.warn('matroska SubtitleParser not constructible from this build:', e && e.message); return false; }
                }),
                tryImport([
                    // Pinned 1.7.17: newer jassub enables useLocalFonts +
                    // WebCodecs paths that break in our setup (postMessage a
                    // function ref; tainted cross-origin video).
                    'https://esm.sh/jassub@1.7.17?bundle&target=esnext',
                    'https://esm.sh/jassub@1.7.17?target=esnext',
                    'https://esm.run/jassub@1.7.17',
                    'https://cdn.skypack.dev/jassub@1.7.17',
                ]),
            ]);
            var m = pair[0], j = pair[1];
            if (m) {
                var Parser = m.SubtitleParser || (m.default && m.default.SubtitleParser) || m.default;
                if (typeof Parser === 'function') { window.MatroskaSubtitles = { SubtitleParser: Parser }; status.matroska = true; }
                else console.warn('matroska-subtitles: SubtitleParser export not found; module shape:', Object.keys(m || {}));
            }
            if (j) {
                var JassubCtor = (typeof j === 'function' && j) || j.default || j.JASSUB;
                if (typeof JassubCtor === 'function') { window.JASSUB = JassubCtor; status.jassub = true; }
                else console.warn('jassub: constructor export not found; module shape:', Object.keys(j || {}));
            }
            return status;
        })();
        return _libsReady;
    }

    // ── Embedded MKV subtitle extraction (streaming matroska-subtitles) ───────
    function extractEmbeddedSubs(url, onTracks, onComplete) {
        var abortCtl = new AbortController();

        // Bandwidth-aware short-circuit. Pulling the MKV through cf-cors-proxy can
        // be 700 MB–1.5 GB; skip on cellular / Data Saver / slow / mobile-unknown.
        var skipReason = networkSkipReason();
        if (skipReason) {
            var label = {
                'data-saver': 'Data Saver on',
                'cellular': 'cellular connection',
                'slow-connection': 'slow connection',
                'low-bandwidth': 'low bandwidth',
                'mobile-unknown-network': "couldn't confirm wifi on mobile",
            }[skipReason] || skipReason;
            try { console.info('[AniSync] embedded extraction skipped:', skipReason); } catch (_) { }
            setEmbedStatus('Skipped — ' + label, 'none');
            onComplete([]);
            return abortCtl;
        }

        // Resolve the URL to fetch from. CORS-blocked hosts (debrid CDNs +
        // Torrentio resolver) need the cf-cors-proxy; without it, skip silently.
        var urlPromise;
        if (isCorsBlockedHost(url)) {
            if (!ctx.corsProxyUrl) {
                setEmbedStatus(null);
                onComplete([]);
                return abortCtl;
            }
            setEmbedStatus('Waiting for stream URL…', 'loading');
            var originalUrl = url;
            urlPromise = resolveStreamUrl(originalUrl, abortCtl.signal).then(function (resolved) {
                return { direct: resolved, stream: viaCorsProxy(resolved) || viaCorsProxy(originalUrl) };
            });
        } else {
            urlPromise = Promise.resolve({ direct: url, stream: url });
        }

        urlPromise.then(function (urls) {
            if (abortCtl.signal.aborted) return;
            setEmbedStatus('Looking for embedded subtitles…', 'loading');
            url = urls.stream;
            loadSubtitleLibs().then(function (status) {
                if (abortCtl.signal.aborted) return;
                if (!status.matroska || typeof MatroskaSubtitles === 'undefined' || !MatroskaSubtitles.SubtitleParser) {
                    setEmbedStatus('Embedded-subtitle support unavailable', 'error');
                    onComplete([]);
                    return;
                }
                runExtraction();
            }).catch(function (e) {
                console.warn('embedded sub readiness chain failed:', e);
                onComplete([]);
            });
        }, function (err) {
            if (abortCtl.signal.aborted) return;
            console.info('embedded sub extraction skipped — resolve never returned a CDN URL:', err && err.message);
            setEmbedStatus(null);
            onComplete([]);
        });
        return abortCtl;

        function runExtraction() {
            if (abortCtl.signal.aborted) return;
            var parser;
            try { parser = new MatroskaSubtitles.SubtitleParser(); }
            catch (e) {
                var msg = (e && e.message) ? String(e.message) : 'unknown error';
                console.warn('matroska parser init threw:', e);
                setEmbedStatus('Parser init failed: ' + msg.slice(0, 80), 'error');
                onComplete([]);
                return;
            }

            var trackMeta = [];   // [{ number, language, codecID, header }]
            var trackCues = {};   // number → array of { time, duration, text }
            var targetTrackNumber = -1;

            var EARLY_EXIT_TAIL_FRACTION = 0.85;
            var EARLY_EXIT_GAP_IN_TAIL = 30 * 1024 * 1024;
            var EARLY_EXIT_GAP_NO_LENGTH = 400 * 1024 * 1024;
            var totalBytesRead = 0;
            var contentLength = 0;
            var lastCueByteOffset = 0;
            var seenAnyCue = false;
            var earlyExited = false;

            function isEnglishTrack(t) {
                var l = (t.language || '').toLowerCase();
                var n = (t.name || '').toLowerCase();
                return l.indexOf('en') === 0 || /\benglish\b/.test(n) || /\beng\b/.test(n);
            }

            parser.once('tracks', function (tracks) {
                trackMeta = tracks.map(function (t) {
                    var name = t.name || '';
                    return {
                        number: t.number,
                        language: t.language || 'und',
                        name: name,
                        codecID: t.type || '',
                        header: t.header || '',
                        label: name || prettyLanguage(t.language),
                    };
                });
                try {
                    console.log('[AniSync] embedded tracks seen:', trackMeta.map(function (t) {
                        return {
                            number: t.number, language: t.language, codecID: t.codecID,
                            headerLength: (t.header || '').length,
                            hasStyles: (t.header || '').indexOf('[V4+ Styles]') >= 0,
                        };
                    }));
                } catch (_) { }

                var english = null;
                for (var i = 0; i < trackMeta.length; i++) {
                    if (isEnglishTrack(trackMeta[i])) { english = trackMeta[i]; break; }
                }
                if (english) { targetTrackNumber = english.number; trackMeta = [english]; }
                else { targetTrackNumber = 0; trackMeta = []; }

                if (trackMeta.length > 0) {
                    setEmbedStatus('Found embedded ' + (english.label || 'English') + ' track — parsing cues…', 'loading');
                } else {
                    setEmbedStatus('No English embedded subtitle in this file', 'none');
                }
                onTracks(trackMeta);

                if (targetTrackNumber === 0) {
                    earlyExited = true;
                    try { parser.end(); } catch (_) { }
                }
            });
            parser.on('subtitle', function (sub, trackNumber) {
                seenAnyCue = true;
                lastCueByteOffset = totalBytesRead;
                if (targetTrackNumber > 0 && trackNumber !== targetTrackNumber) return;
                (trackCues[trackNumber] = trackCues[trackNumber] || []).push(sub);
            });
            parser.on('finish', function () {
                var output = trackMeta.map(function (t) {
                    var cues = trackCues[t.number] || [];
                    return Object.assign({}, t, { data: buildAssDocument(t.header, cues), cues: cues });
                });
                try {
                    console.log('[AniSync] embedded extraction finished:', output.map(function (t) {
                        return { number: t.number, language: t.language, codecID: t.codecID, cueCount: (trackCues[t.number] || []).length, dataLength: t.data.length };
                    }));
                } catch (_) { }
                if (output.length > 0) {
                    setEmbedStatus(output.length + ' embedded track' + (output.length === 1 ? '' : 's') + ' ready', 'success');
                } else {
                    setEmbedStatus('No embedded subtitles in this file', 'none');
                }
                onComplete(output);
            });
            parser.on('error', function (e) {
                console.warn('matroska parser error:', e);
                setEmbedStatus('Embedded-subtitle parse failed', 'error');
                onComplete([]);
            });

            fetch(url, { signal: abortCtl.signal })
                .then(function (response) {
                    if (!response.ok || !response.body) { onComplete([]); return; }
                    try {
                        var lenHeader = response.headers.get('content-length');
                        if (lenHeader) contentLength = parseInt(lenHeader, 10) || 0;
                    } catch (_) { }
                    try {
                        console.log('[AniSync] embedded sub stream start: content-length=' +
                            (contentLength ? Math.round(contentLength / (1024 * 1024)) + ' MB' : 'unknown'));
                    } catch (_) { }
                    var reader = response.body.getReader();
                    function pump() {
                        if (earlyExited) { try { reader.cancel(); } catch (_) { } return; }
                        return reader.read().then(function (result) {
                            if (result.done) { try { parser.end(); } catch (_) { } return; }
                            totalBytesRead += result.value.byteLength;
                            try { parser.write(result.value); } catch (e) { /* parser may abort early at EOS */ }
                            var gap = totalBytesRead - lastCueByteOffset;
                            var canBail;
                            if (contentLength > 0) {
                                var inTail = totalBytesRead > contentLength * EARLY_EXIT_TAIL_FRACTION;
                                canBail = inTail && gap > EARLY_EXIT_GAP_IN_TAIL;
                            } else {
                                canBail = gap > EARLY_EXIT_GAP_NO_LENGTH;
                            }
                            if (seenAnyCue && canBail) {
                                try {
                                    console.log('[AniSync] embedded sub early-exit: ' +
                                        Math.round(totalBytesRead / (1024 * 1024)) + ' MB read' +
                                        (contentLength ? ' / ' + Math.round(contentLength / (1024 * 1024)) + ' MB' : '') +
                                        ', ' + Math.round(gap / (1024 * 1024)) + ' MB since last cue; flushing parser.');
                                } catch (_) { }
                                earlyExited = true;
                                try { reader.cancel(); } catch (_) { }
                                try { parser.end(); } catch (_) { }
                                return;
                            }
                            return pump();
                        });
                    }
                    return pump();
                })
                .catch(function (e) {
                    if (!e || e.name === 'AbortError') {
                        // expected — source switch / unmount
                    } else if (e.name === 'TypeError') {
                        console.info('embedded sub stream unreachable (likely CORS on debrid CDN):', e.message);
                        setEmbedStatus(null);
                    } else {
                        console.warn('embedded sub stream failed:', e);
                        setEmbedStatus('Embedded-subtitle fetch failed', 'error');
                    }
                    onComplete([]);
                });
        } // end runExtraction
    }

    function prettyLanguage(code) {
        var c = (code || '').toLowerCase();
        var map = {
            eng: 'English', spa: 'Spanish', por: 'Portuguese',
            fre: 'French', fra: 'French', ger: 'German', deu: 'German',
            ita: 'Italian', rus: 'Russian', ara: 'Arabic',
            jpn: 'Japanese', chi: 'Chinese', zho: 'Chinese',
            kor: 'Korean', tur: 'Turkish', pol: 'Polish',
            dut: 'Dutch', nld: 'Dutch', und: 'Embedded',
        };
        return map[c] || (code ? code.toUpperCase() : 'Embedded');
    }

    var MINIMAL_ASS_HEADER = [
        '[Script Info]', 'ScriptType: v4.00+', 'PlayResX: 1920', 'PlayResY: 1080',
        'ScaledBorderAndShadow: yes', '', '[V4+ Styles]',
        'Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding',
        'Style: Default,Arial,42,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,40,40,60,1',
        '', '[Events]',
        'Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text',
    ].join('\n');

    function buildAssDocument(header, cues) {
        var hasStyles = header && header.indexOf('[V4+ Styles]') >= 0;
        if (!hasStyles) header = MINIMAL_ASS_HEADER;
        if (!cues || cues.length === 0) return header;
        var lines = [];
        for (var i = 0; i < cues.length; i++) {
            var c = cues[i];
            var rawText = (c.text || '').trim();
            if (!rawText) continue;
            var start = formatAssTime(c.time);
            var end = formatAssTime(c.time + (c.duration || 0));
            var text = rawText.replace(/\r?\n/g, '\\N');
            lines.push('Dialogue: 0,' + start + ',' + end + ',Default,,0,0,0,,' + text);
        }
        return header + '\n' + lines.join('\n') + '\n';
    }
    function formatAssTime(ms) {
        if (!ms || ms < 0) ms = 0;
        var h = Math.floor(ms / 3600000);
        var m = Math.floor((ms % 3600000) / 60000);
        var s = Math.floor((ms % 60000) / 1000);
        var cs = Math.floor((ms % 1000) / 10);
        return h + ':' + pad2(m) + ':' + pad2(s) + '.' + pad2(cs);
    }
    function pad2(n) { return (n < 10 ? '0' : '') + n; }

    // ── ASS → WebVTT converter (\an / \pos / \move positioning preserved) ─────
    var ASS_TIME_RE = /^(\d+):(\d+):(\d+)\.(\d+)$/;
    var ASS_OVERRIDE_RE = /\{[^}]*\}/g;
    function assTimeToSeconds(s) {
        var m = ASS_TIME_RE.exec((s || '').trim());
        if (!m) return null;
        var h = parseInt(m[1], 10);
        var mm = parseInt(m[2], 10);
        var ss = parseInt(m[3], 10);
        var frac = (m[4] + '00').slice(0, 3);
        return h * 3600 + mm * 60 + ss + parseInt(frac, 10) / 1000;
    }
    function vttTimestamp(totalSec) {
        if (!isFinite(totalSec) || totalSec < 0) totalSec = 0;
        var h = Math.floor(totalSec / 3600);
        var m = Math.floor((totalSec % 3600) / 60);
        var s = totalSec % 60;
        var ms = Math.round((s - Math.floor(s)) * 1000);
        s = Math.floor(s);
        if (ms === 1000) { ms = 0; s++; }
        function pad(n, w) { var x = String(n); while (x.length < w) x = '0' + x; return x; }
        return pad(h, 2) + ':' + pad(m, 2) + ':' + pad(s, 2) + '.' + pad(ms, 3);
    }
    function parseAssPlayRes(assText) {
        var x = 1920, y = 1080;
        if (!assText) return { x: x, y: y };
        var lines = assText.split('\n');
        for (var i = 0; i < lines.length; i++) {
            var ln = lines[i];
            var mx = /^\s*PlayResX\s*:\s*(\d+)/i.exec(ln);
            if (mx) x = parseInt(mx[1], 10) || x;
            var my = /^\s*PlayResY\s*:\s*(\d+)/i.exec(ln);
            if (my) y = parseInt(my[1], 10) || y;
            if (/^\s*\[/.test(ln) && !/Script Info/i.test(ln)) break;
        }
        return { x: x, y: y };
    }
    function vttSettingsForAlign(an) {
        var line, position, align;
        if (an === 7 || an === 8 || an === 9) line = '5%';
        else if (an === 4 || an === 5 || an === 6) line = '50%';
        else line = null;
        if (an === 1 || an === 4 || an === 7) { position = '5%'; align = 'start'; }
        else if (an === 3 || an === 6 || an === 9) { position = '95%'; align = 'end'; }
        else { position = null; align = null; }
        return { line: line, position: position, align: align };
    }
    function buildVttCueSettings(overrideText, playRes) {
        if (!overrideText) return '';
        var anMatch = /\\an([1-9])/.exec(overrideText);
        var aMatch = /\\a(\d+)/.exec(overrideText);
        var posMatch = /\\pos\(\s*([\d.\-]+)\s*,\s*([\d.\-]+)\s*\)/.exec(overrideText);
        var moveMatch = /\\move\(\s*([\d.\-]+)\s*,\s*([\d.\-]+)/.exec(overrideText);
        var line = null, position = null, align = null;
        var posSource = posMatch || moveMatch;
        if (posSource) {
            var px = (parseFloat(posSource[1]) / playRes.x) * 100;
            var py = (parseFloat(posSource[2]) / playRes.y) * 100;
            if (px < 0) px = 0; else if (px > 100) px = 100;
            if (py < 0) py = 0; else if (py > 100) py = 100;
            position = px.toFixed(1) + '%';
            line = py.toFixed(1) + '%';
            if (anMatch) {
                var anA = parseInt(anMatch[1], 10);
                if (anA === 1 || anA === 4 || anA === 7) align = 'start';
                else if (anA === 3 || anA === 6 || anA === 9) align = 'end';
                else align = 'center';
            }
        } else if (anMatch) {
            var sA = vttSettingsForAlign(parseInt(anMatch[1], 10));
            line = sA.line; position = sA.position; align = sA.align;
        } else if (aMatch) {
            var v = parseInt(aMatch[1], 10);
            var legacyToAn = { 1: 1, 2: 2, 3: 3, 5: 7, 6: 8, 7: 9, 9: 4, 10: 5, 11: 6 };
            var mapped = legacyToAn[v];
            if (mapped) {
                var s2 = vttSettingsForAlign(mapped);
                line = s2.line; position = s2.position; align = s2.align;
            }
        }
        var parts = [];
        if (line) parts.push('line:' + line);
        if (position) parts.push('position:' + position);
        if (align) parts.push('align:' + align);
        return parts.length ? ' ' + parts.join(' ') : '';
    }
    function assToVtt(assText) {
        if (!assText) return 'WEBVTT\n\n';
        var playRes = parseAssPlayRes(assText);
        var lines = assText.replace(/\r\n/g, '\n').split('\n');
        var out = ['WEBVTT', ''];
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i];
            if (!/^Dialogue:/i.test(line)) continue;
            var body = line.replace(/^Dialogue:\s*/i, '');
            var commas = 0, split = -1;
            for (var j = 0; j < body.length && commas < 9; j++) {
                if (body[j] === ',') { commas++; if (commas === 9) split = j; }
            }
            if (split < 0) continue;
            var fields = body.substring(0, split).split(',');
            if (fields.length < 9) continue;
            var start = assTimeToSeconds(fields[1]);
            var end = assTimeToSeconds(fields[2]);
            if (start === null || end === null || end <= start) continue;
            var rawText = body.substring(split + 1);
            var overrideText = (rawText.match(ASS_OVERRIDE_RE) || []).join('');
            var text = rawText.replace(ASS_OVERRIDE_RE, '').replace(/\\N/g, '\n').replace(/\\h/g, ' ').trim();
            if (!text) continue;
            var settings = buildVttCueSettings(overrideText, playRes);
            if (!/(^|\s)line:/.test(settings)) settings += ' line:-2';
            out.push(vttTimestamp(start) + ' --> ' + vttTimestamp(end) + settings);
            out.push(text);
            out.push('');
        }
        return out.join('\n');
    }

    var _vttBlobCache = {};
    function renderAssAsVtt(item) {
        if (!art || !item || !item.trackData) return;
        var cacheKey = item.trackData.length + ':' + (item.html || '');
        var url = _vttBlobCache[cacheKey];
        if (!url) {
            var vtt = assToVtt(item.trackData);
            url = URL.createObjectURL(new Blob([vtt], { type: 'text/vtt' }));
            _vttBlobCache[cacheKey] = url;
        }
        setActiveSubtitleTrack(url, (item.html || 'Embedded') + ' (VTT)', item.lang);
    }

    // ── ArtPlayer mount (the engine) ──────────────────────────────────────────
    // elementId: the container <div>; options: { url, resumeSeconds, subtitles[] };
    // dotnet: DotNetObjectReference exposing OnProgress(pos,dur) + OnEnded() +
    // OnStreamEvent(kind, reason). Returns false when the container is absent or
    // ArtPlayer hasn't loaded yet (defensive — the caller logged playingUrl, the
    // page surfaces its own state).
    function play(elementId, options, dotnet) {
        var host = document.getElementById(elementId);
        if (!host) return false;
        if (typeof window.Artplayer === 'undefined') {
            // ArtPlayer (the deferred CDN script) hasn't finished loading yet. A
            // deferred <script> executes before DOMContentLoaded — long before a
            // source-row click — so this is rare, but a cold cache + a fast click
            // can race it. Poll briefly for the global, then mount; if it never
            // arrives within ~6s, surface a timeout fallback (Retry / Open
            // externally) rather than hang on a black frame.
            dotnetRef = dotnet || null;
            var waited = 0;
            var poll = setInterval(function () {
                waited += 150;
                if (typeof window.Artplayer !== 'undefined') {
                    clearInterval(poll);
                    play(elementId, options, dotnet);
                } else if (waited >= 6000) {
                    clearInterval(poll);
                    console.warn('[AniSync] ArtPlayer failed to load');
                    emit('timeout');
                }
            }, 150);
            return true;
        }

        // New source pick — tear down the previous player + extraction cleanly.
        destroyPlayer();
        dotnetRef = dotnet || null;
        osSubtitles = (options && Array.isArray(options.subtitles)) ? options.subtitles : [];
        watchdogMs = IS_IOS_SAFARI ? 8000 : 25000;
        currentSubKind = 'none';
        currentSubKey = null;

        var streamUrl = options && options.url ? options.url : '';
        var resume = options && options.resumeSeconds ? options.resumeSeconds : 0;

        // Default subtitle: first English entry when present, else None. We don't
        // fall back to the first non-English track — a surprise foreign overlay is
        // worse than none; the user can still pick any track manually.
        var defaultSubIdx = -1;
        for (var i = 0; i < osSubtitles.length; i++) {
            if (/^en/i.test(osSubtitles[i].lang || osSubtitles[i].language || '')) { defaultSubIdx = i; break; }
        }
        if (defaultSubIdx >= 0) {
            currentSubKind = 'vtt';
            currentSubKey = osSubtitles[defaultSubIdx].url;
        }

        // Subtitle selector — always a "None" entry, then embedded SSA, then OS VTT.
        buildSubtitleSelector = function () {
            var opts = [{ html: 'None', kind: 'none', "default": currentSubKind === 'none' }];
            embeddedTracks.forEach(function (t) {
                var label = (t.label || 'Embedded') + ' SSA';
                opts.push({ html: label, kind: 'ass', trackData: t.data, "default": currentSubKind === 'ass' && currentSubKey === label });
            });
            osSubtitles.forEach(function (t) {
                opts.push({ html: t.label || t.lang || 'Subtitles', kind: 'vtt', url: t.url, lang: t.lang || t.language, "default": currentSubKind === 'vtt' && currentSubKey === t.url });
            });
            return opts;
        };

        var settings = [{
            name: 'subtitles', html: 'Subtitles', tooltip: 'Subtitle track',
            selector: buildSubtitleSelector(),
            onSelect: function (item) { applySubtitle(item); return item.html; },
        }];

        // AniSkip OP / ED ticks on the progress bar (the visual cue; the corner
        // Skip button — painted by Watch.razor — is the action).
        var highlight = [];
        var times = options && options.skipTimes ? options.skipTimes : (ctx.skipTimes || null);
        if (times) {
            if (times.intro) highlight.push({ time: times.intro.start, text: 'OP' });
            if (times.outro) highlight.push({ time: times.outro.start, text: 'ED' });
        }

        var isMobile = false;
        try { isMobile = window.matchMedia('(max-width: 640px)').matches; } catch (_) { }

        var rewindSvg = '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="11 17 6 12 11 7"/><polyline points="18 17 13 12 18 7"/></svg>';
        var forwardSvg = '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="13 17 18 12 13 7"/><polyline points="6 17 11 12 6 7"/></svg>';

        try {
            art = new window.Artplayer({
                container: host,
                url: streamUrl,
                title: (options && options.title) || ctx.title || '',
                theme: '#7c3aed',
                autoplay: true,
                pip: false,
                screenshot: false,
                setting: true,
                playbackRate: true,
                aspectRatio: false,
                fullscreen: true,
                fullscreenWeb: !isMobile,
                subtitleOffset: true,
                miniProgressBar: false,
                gesture: false,
                autoOrientation: true,
                hotkey: true,
                airplay: true,
                moreVideoAttr: {
                    playsInline: true,
                    disablePictureInPicture: true,
                    preload: 'auto',
                },
                // Subtitle rendering goes through setActiveSubtitleTrack (native
                // <track>) so cue settings are honoured; pass an empty subtitle
                // config so ArtPlayer doesn't double-render its own overlay.
                subtitle: {},
                settings: settings,
                highlight: highlight,
                controls: [
                    {
                        position: 'left', index: 8, html: rewindSvg, tooltip: 'Rewind 10s',
                        click: function () { var v = art && art.video; if (v) art.seek = Math.max(0, (v.currentTime || 0) - 10); },
                    },
                    {
                        position: 'left', index: 12, html: forwardSvg, tooltip: 'Forward 10s',
                        click: function () {
                            var v = art && art.video; if (!v) return;
                            var target = (v.currentTime || 0) + 10;
                            if (v.duration && target > v.duration) target = v.duration;
                            art.seek = target;
                        },
                    },
                ],
            });
        } catch (e) {
            console.warn('[AniSync] ArtPlayer init failed:', e);
            emit('error', 'decode');
            return false;
        }

        // Capture-phase touch guard: seal the mobile scrub-while-hidden race.
        // ArtPlayer binds touchstart/touchmove to .art-progress unconditionally on
        // mobile; this stopImmediatePropagation()s touches targeting .art-progress
        // while the player isn't in the hover state (self-disables once .art-hover
        // is present), preempting ArtPlayer's bubbling-phase listener.
        try {
            var $player = art.template && art.template.$player;
            if ($player) {
                $player.addEventListener('touchstart', function (e) {
                    if ($player.classList.contains('art-hover')) return;
                    var t = e.target;
                    while (t && t !== $player) {
                        if (t.classList && t.classList.contains('art-progress')) { e.stopImmediatePropagation(); return; }
                        t = t.parentNode;
                    }
                }, { capture: true, passive: true });
            }
        } catch (_) { /* defensive — non-fatal if template shape changes */ }

        // Prime the first subtitle track manually (subtitle: {} means ArtPlayer
        // didn't load one of its own).
        if (defaultSubIdx >= 0) {
            var ds = osSubtitles[defaultSubIdx];
            setActiveSubtitleTrack(ds.url, ds.label || ds.lang || ds.language, ds.lang || ds.language);
        }

        // ── video:error → 'network' (code 2) | 'decode' (3 / 4); 1 ABORTED ignored.
        art.on('video:error', function () {
            cancelWatchdog();
            var v = art.video;
            var code = v && v.error && v.error.code;
            if (code === 1) return;
            emit('error', code === 2 ? 'network' : 'decode');
        });

        // Buffer-underrun watchdog (waiting / stalled clustering).
        art.on('video:waiting', function () { recordBufferStall('waiting'); });
        art.on('video:stalled', function () { recordBufferStall('stalled'); });

        // Stream watchdog — nothing decoded within STREAM_WATCHDOG_MS → 'timeout'.
        startWatchdog();
        art.on('video:loadedmetadata', cancelWatchdog);
        art.on('video:playing', function () { cancelWatchdog(); emit('playing'); });

        // ── Progress → .NET (resume + mark-watched policy lives in C#). ~1/sec.
        var lastTick = -1;
        art.on('video:timeupdate', function () {
            var v = art && art.video;
            if (!v) return;
            var now = v.currentTime || 0;
            if (lastTick >= 0 && Math.abs(now - lastTick) < 1) return;
            lastTick = now;
            if (dotnetRef) {
                dotnetRef.invokeMethodAsync('OnProgress', now, isFinite(v.duration) ? v.duration : 0)
                    .catch(function () { /* circuit gone */ });
            }
        });

        // Resume seek on metadata load (first event with a real duration +
        // seekable <video>, so the jump happens before playback is visible).
        var hasResumedThisSource = false;
        art.on('video:loadedmetadata', function () {
            if (hasResumedThisSource) return;
            hasResumedThisSource = true;
            var v = art && art.video;
            if (!v) return;
            var dur = v.duration;
            if (!isFinite(dur) || dur <= 60) return;
            if (resume <= 0 || resume >= dur) return;
            try {
                art.seek = resume;
                if (art.notice) art.notice.show = 'Resumed from ' + formatResumeTime(resume);
            } catch (_) { /* defensive — non-fatal */ }
        });

        art.on('video:ended', function () {
            if (dotnetRef) dotnetRef.invokeMethodAsync('OnEnded').catch(function () { });
        });

        // Kick off embedded MKV subtitle extraction in the background. Track
        // metadata surfaces as soon as the parser hits the Tracks element; full
        // ASS bodies finalise on completion. Rebuild the selector twice (metadata
        // + completion) so entries appear as they become usable.
        embeddedExtractAbort = extractEmbeddedSubs(
            streamUrl,
            function (meta) {
                embeddedTracks = meta.map(function (t) { return Object.assign({}, t, { data: '' }); });
                refreshSubtitleSelector(buildSubtitleSelector);
            },
            function (full) {
                embeddedTracks = full;
                // Auto-promote ONLY an English embedded track (language tag OR
                // TrackName — fansubs leave Language="und" but set the name).
                var englishTrack = null;
                for (var i = 0; i < full.length; i++) {
                    var t = full[i];
                    var lang = (t.language || '').toLowerCase();
                    var name = (t.name || '').toLowerCase();
                    if (lang.indexOf('en') === 0 || /\benglish\b/.test(name) || /\beng\b/.test(name)) { englishTrack = t; break; }
                }
                if (englishTrack && englishTrack.data) {
                    applySubtitle({ kind: 'ass', html: (englishTrack.label || 'Embedded') + ' SSA', trackData: englishTrack.data });
                    try { console.info('[AniSync] embedded auto-promoted — ' + (englishTrack.cues || []).length + ' cues, OS pool size=' + osSubtitles.length); } catch (_) { }
                    setupEmbeddedToOsHandover(englishTrack, osSubtitles, function () { refreshSubtitleSelector(buildSubtitleSelector); });
                } else {
                    try { console.info('[AniSync] no English embedded track to auto-promote (full=' + full.length + ')'); } catch (_) { }
                }
                refreshSubtitleSelector(buildSubtitleSelector);
            });

        // Fetch release-matched OpenSubtitles for the chip + (if the page didn't
        // pass any) the selector. Best-effort; mirrors the original's
        // /episode-subtitles fetch + providerCounts chip render.
        fetchSubtitlesForChip(options);

        return true;
    }

    function formatResumeTime(seconds) {
        seconds = Math.max(0, Math.floor(seconds || 0));
        var h = Math.floor(seconds / 3600);
        var m = Math.floor((seconds % 3600) / 60);
        var s = seconds % 60;
        var pad = function (n) { return n < 10 ? '0' + n : '' + n; };
        return h > 0 ? h + ':' + pad(m) + ':' + pad(s) : m + ':' + pad(s);
    }

    // Surface the OpenSubtitles provider chip. The page already passes the OS list
    // (via options.subtitles / Api.SubtitlesAsync) so the selector is populated;
    // this only renders the count chip. When the page didn't pre-resolve subtitles
    // (e.g. native-style call), fall back to a release-matched fetch so the chip +
    // selector still populate. Best-effort throughout.
    function fetchSubtitlesForChip(options) {
        // If the page already passed OS tracks, render the chip from their count
        // and skip the extra round-trip.
        if (osSubtitles.length > 0) {
            renderSubsProviderChip({ opensubtitles: osSubtitles.length });
            return;
        }
        if (!ctx.id) return;
        var filename = (options && options.filename) || ctx.filename || '';
        var qs = 'id=' + encodeURIComponent(ctx.id) + '&episode=' + encodeURIComponent(ctx.episode);
        if (ctx.season != null) qs += '&season=' + encodeURIComponent(ctx.season);
        if (filename) qs += '&filename=' + encodeURIComponent(filename);
        if (ctx.type === 'movie') qs += '&type=movie';
        apiFetch('/api/v1/me/episode-subtitles?' + qs)
            .then(function (r) { return r.ok ? r.json() : { subtitles: [] }; })
            .catch(function () { return { subtitles: [] }; })
            .then(function (data) {
                var subs = (data && data.subtitles) || [];
                var pc = (data && data.providerCounts) || { opensubtitles: subs.length };
                try { console.log('[AniSync] subtitles loaded:', subs.length, 'tracks', subs); } catch (_) { }
                renderSubsProviderChip(pc);
                if (subs.length > 0) {
                    osSubtitles = subs;
                    // Auto-pick the first English track if nothing is active yet.
                    if (currentSubKind === 'none') {
                        for (var i = 0; i < subs.length; i++) {
                            if (/^en/i.test(subs[i].lang || '')) {
                                applySubtitle({ kind: 'vtt', url: subs[i].url, html: subs[i].label || subs[i].lang, lang: subs[i].lang });
                                break;
                            }
                        }
                    }
                    if (buildSubtitleSelector) refreshSubtitleSelector(buildSubtitleSelector);
                }
            });
    }

    // ── Public contract (unchanged) ───────────────────────────────────────────
    function seek(seconds) {
        if (art) { try { art.seek = seconds; } catch (_) { } }
    }

    // index < 0 disables subtitles; otherwise selects the OS VTT track at that
    // index (the page's <select> indexes into the OS list). Mirrors the original
    // setSubtitle contract while routing through applySubtitle so the native
    // <track> renderer stays the single source of truth.
    function setSubtitle(index) {
        if (!art) return;
        if (index < 0 || index >= osSubtitles.length) {
            applySubtitle({ kind: 'none' });
        } else {
            var t = osSubtitles[index];
            applySubtitle({ kind: 'vtt', url: t.url, html: t.label || t.lang, lang: t.lang || t.language });
        }
        if (buildSubtitleSelector) refreshSubtitleSelector(buildSubtitleSelector);
    }

    function stop() {
        destroyPlayer();
        setEmbedStatus(null);
        dotnetRef = null;
    }

    // ── Toolbar wiring (web head; idempotent, re-callable per render) ─────────
    // Two browser-only concerns the C# toolbar can't do itself:
    //   1. Relocate the C#-rendered #watchSkipButton into ArtPlayer's $player so it
    //      stays inside the fullscreen viewport (both elements are position:relative,
    //      the button position:absolute, so corner-anchoring is identical — but only
    //      $player is the element ArtPlayer toggles fullscreen on). Verbatim from the
    //      original's `$playerHost.appendChild(skipButton)`.
    //   2. Reveal + wire the #watchEmbedMobileOptIn toggle. classifyNetwork() (the
    //      RAW signal — not networkSkipReason) tells us whether the user is in a
    //      state we'd normally skip (confirmed cellular / wifi-vs-cell ambiguity);
    //      the toggle is only meaningful there, so it stays hidden otherwise. The
    //      pref is read on extraction; flipping it applies on the next source pick.
    var _optInWired = false;
    function wireToolbar() {
        // Relocate the skip button (re-run safe — appendChild is a move, not a copy).
        try {
            var skipButton = document.getElementById('watchSkipButton');
            var $player = art && art.template && art.template.$player;
            if (skipButton && $player && skipButton.parentNode !== $player) {
                $player.appendChild(skipButton);
                // ArtPlayer's player surface has a global click handler that toggles
                // play/pause; without stopPropagation a Skip click would seek AND
                // pause. The seek itself is the C# @onclick — we only guard the
                // bubble. Bind once per button instance.
                if (!skipButton.dataset.guardBound) {
                    skipButton.dataset.guardBound = '1';
                    skipButton.addEventListener('click', function (e) { e.stopPropagation(); });
                }
            }
        } catch (_) { }

        // Reveal + wire the mobile opt-in once.
        if (_optInWired) return;
        var btn = document.getElementById('watchEmbedMobileOptIn');
        if (!btn) return;
        var rawNet = classifyNetwork();
        if (!OPTIN_OVERRIDES[rawNet]) return; // not a skip-prone state — leave hidden
        _optInWired = true;
        var optIn = false;
        try { optIn = localStorage.getItem(EMBED_MOBILE_OPTIN_KEY) === '1'; } catch (_) { }
        btn.hidden = false;
        btn.setAttribute('aria-pressed', optIn ? 'true' : 'false');
        btn.classList.toggle('is-active', optIn);
        btn.addEventListener('click', function () {
            optIn = !optIn;
            try { localStorage.setItem(EMBED_MOBILE_OPTIN_KEY, optIn ? '1' : '0'); } catch (_) { }
            btn.setAttribute('aria-pressed', optIn ? 'true' : 'false');
            btn.classList.toggle('is-active', optIn);
        });
    }

    return { setContext: setContext, play: play, seek: seek, setSubtitle: setSubtitle, stop: stop, wireToolbar: wireToolbar };
})();
