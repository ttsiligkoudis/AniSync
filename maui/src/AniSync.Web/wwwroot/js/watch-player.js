// Web-head media-player interop. The shared Watch page resolves IMediaPlayer to
// Html5MediaPlayer on the browser; that C# service drives the page's <video>
// element through these functions so resume, progress (auto-track) and ended
// (auto-play-next) work the same way the native LibVLCSharp head does — keeping
// all the resume/scrobble policy in the shared C# layer.
//
// Stage-3a additions (player-glue): the bare <video> can't speak ArtPlayer's
// custom events, so this module mirrors the original Watch.cshtml's stream
// watchdog + error/stall surfacing here and forwards a single onStreamEvent
// callback to .NET (Watch.razor renders the fallback panel). The watchdog timing
// (25s desktop / 8s iOS) and the iOS-MKV fast-fail match the original's
// startStreamWatchdog / showFallback wiring.
window.anisyncWatch = (function () {
    let current = null; // { video, dotnet, onTime, onEnded, onLoaded, onError, onPlaying, onWaiting, onStalled }
    let watchdog = null;
    let stallTimes = [];
    let stallHintShown = false;
    let watchdogMs = 25000;

    // iOS Safari can't demux MKV / AVI inline at all, and never fires a media
    // error for it — the <video> just sits black. Mirrors IS_IOS_SAFARI in the
    // original so the watchdog uses the tighter 8s budget there.
    function isIosSafari() {
        let ua = '';
        try { ua = navigator.userAgent || ''; } catch (e) { }
        if (/iPad|iPhone|iPod/.test(ua)) return true;
        if (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1) return true;
        return false;
    }

    function cancelWatchdog() {
        if (watchdog) { clearTimeout(watchdog); watchdog = null; }
    }

    // Surface a stream event back to .NET. kind: 'error' | 'timeout' | 'playing'
    // | 'stall'. reason (for 'error'): 'network' | 'decode'.
    function emit(kind, reason) {
        if (current && current.dotnet) {
            current.dotnet.invokeMethodAsync('OnStreamEvent', kind, reason || null)
                .catch(() => { /* circuit gone */ });
        }
    }

    function startWatchdog() {
        cancelWatchdog();
        watchdog = setTimeout(function () {
            watchdog = null;
            const v = current && current.video;
            if (!v) return;
            // HAVE_CURRENT_DATA (2)+ means decoding started — leave it alone.
            if (v.readyState >= 2 || v.currentTime > 0) return;
            emit('timeout');
        }, watchdogMs);
    }

    // Buffer-underrun heuristic mirrored from the original: surface a hint once
    // 3 stalls cluster inside a 30s window, then stay quiet for the session.
    function recordStall() {
        const now = Date.now();
        stallTimes.push(now);
        while (stallTimes.length && now - stallTimes[0] > 30000) stallTimes.shift();
        if (stallTimes.length >= 3 && !stallHintShown) {
            stallHintShown = true;
            emit('stall');
        }
    }

    function detach() {
        if (!current) return;
        const v = current.video;
        try {
            v.removeEventListener('timeupdate', current.onTime);
            v.removeEventListener('ended', current.onEnded);
            v.removeEventListener('loadedmetadata', current.onLoaded);
            v.removeEventListener('error', current.onError);
            v.removeEventListener('playing', current.onPlaying);
            v.removeEventListener('waiting', current.onWaiting);
            v.removeEventListener('stalled', current.onStalled);
        } catch (e) { /* element already gone */ }
        current = null;
        cancelWatchdog();
        stallTimes = [];
        stallHintShown = false;
    }

    function clearTracks(v) {
        try { Array.from(v.querySelectorAll('track')).forEach(t => t.remove()); } catch (e) { }
    }

    // elementId: the <video> id; options: { url, resumeSeconds, subtitles[] };
    // dotnet: DotNetObjectReference exposing OnProgress(pos,dur) + OnEnded() +
    // OnStreamEvent(kind, reason).
    function play(elementId, options, dotnet) {
        const v = document.getElementById(elementId);
        if (!v) return false;
        detach();

        watchdogMs = isIosSafari() ? 8000 : 25000;

        if (options && options.url && v.getAttribute('src') !== options.url) {
            v.setAttribute('src', options.url);
            v.load();
        }

        // Best-effort external subtitle tracks. Cross-origin VTT needs CORS and
        // SRT won't render via <track>; failures here never break playback.
        clearTracks(v);
        if (options && Array.isArray(options.subtitles)) {
            options.subtitles.forEach((s, i) => {
                try {
                    const t = document.createElement('track');
                    t.kind = 'subtitles';
                    t.label = s.label || ('Subtitle ' + (i + 1));
                    if (s.language) t.srclang = s.language;
                    t.src = s.url;
                    v.appendChild(t);
                    // Start hidden; the page's subtitle selector turns one on.
                    if (v.textTracks[i]) v.textTracks[i].mode = 'disabled';
                } catch (e) { }
            });
        }

        const resume = options && options.resumeSeconds ? options.resumeSeconds : 0;
        const onLoaded = function () {
            cancelWatchdog();
            if (resume > 0 && isFinite(v.duration) && resume < v.duration) {
                try { v.currentTime = resume; } catch (e) { }
            }
        };
        let lastTick = -1;
        const onTime = function () {
            const now = v.currentTime || 0;
            if (lastTick >= 0 && Math.abs(now - lastTick) < 1) return; // ~1/sec interop
            lastTick = now;
            if (current && current.dotnet) {
                current.dotnet.invokeMethodAsync('OnProgress', now, isFinite(v.duration) ? v.duration : 0)
                    .catch(() => { /* circuit gone */ });
            }
        };
        const onEnded = function () {
            if (current && current.dotnet) current.dotnet.invokeMethodAsync('OnEnded').catch(() => { });
        };
        // Map HTMLMediaElement error codes the way the original did:
        //   1 ABORTED → ignore (user/nav cancel)
        //   2 NETWORK → 'network' copy (debrid link likely expired)
        //   3 DECODE / 4 SRC_NOT_SUPPORTED → 'decode' copy (codec/container)
        const onError = function () {
            cancelWatchdog();
            const code = v.error && v.error.code;
            if (code === 1) return;
            emit('error', code === 2 ? 'network' : 'decode');
        };
        const onPlaying = function () {
            cancelWatchdog();
            emit('playing');
        };
        const onWaiting = function () { recordStall(); };
        const onStalled = function () { recordStall(); };

        v.addEventListener('loadedmetadata', onLoaded);
        v.addEventListener('timeupdate', onTime);
        v.addEventListener('ended', onEnded);
        v.addEventListener('error', onError);
        v.addEventListener('playing', onPlaying);
        v.addEventListener('waiting', onWaiting);
        v.addEventListener('stalled', onStalled);
        current = {
            video: v, dotnet: dotnet, onTime: onTime, onEnded: onEnded, onLoaded: onLoaded,
            onError: onError, onPlaying: onPlaying, onWaiting: onWaiting, onStalled: onStalled
        };

        startWatchdog();

        const p = v.play();
        if (p && p.catch) p.catch(() => { /* autoplay blocked — the controls let the user start it */ });
        return true;
    }

    function seek(seconds) {
        if (current && current.video) {
            try { current.video.currentTime = seconds; } catch (e) { }
        }
    }

    // index < 0 disables all tracks; otherwise shows only that one.
    function setSubtitle(index) {
        if (!current || !current.video) return;
        const tracks = current.video.textTracks;
        for (let i = 0; i < tracks.length; i++) {
            tracks[i].mode = (i === index) ? 'showing' : 'disabled';
        }
    }

    function stop() {
        if (current) { try { current.video.pause(); } catch (e) { } }
        detach();
    }

    return { play: play, seek: seek, setSubtitle: setSubtitle, stop: stop };
})();
