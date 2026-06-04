// Web-head media-player interop. The shared Watch page resolves IMediaPlayer to
// Html5MediaPlayer on the browser; that C# service drives the page's <video>
// element through these functions so resume, progress (auto-track) and ended
// (auto-play-next) work the same way the native LibVLCSharp head does — keeping
// all the resume/scrobble policy in the shared C# layer.
window.anisyncWatch = (function () {
    let current = null; // { video, dotnet, onTime, onEnded, onLoaded }

    function detach() {
        if (!current) return;
        const v = current.video;
        try {
            v.removeEventListener('timeupdate', current.onTime);
            v.removeEventListener('ended', current.onEnded);
            v.removeEventListener('loadedmetadata', current.onLoaded);
        } catch (e) { /* element already gone */ }
        current = null;
    }

    function clearTracks(v) {
        try { Array.from(v.querySelectorAll('track')).forEach(t => t.remove()); } catch (e) { }
    }

    // elementId: the <video> id; options: { url, resumeSeconds, subtitles[] };
    // dotnet: DotNetObjectReference exposing OnProgress(pos,dur) + OnEnded().
    function play(elementId, options, dotnet) {
        const v = document.getElementById(elementId);
        if (!v) return false;
        detach();

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
            if (resume > 0 && isFinite(v.duration) && resume < v.duration) {
                try { v.currentTime = resume; } catch (e) { }
            }
        };
        let lastTick = -1;
        const onTime = function () {
            const now = v.currentTime || 0;
            if (lastTick >= 0 && Math.abs(now - lastTick) < 1) return; // ~1/sec interop
            lastTick = now;
            if (dotnet) {
                dotnet.invokeMethodAsync('OnProgress', now, isFinite(v.duration) ? v.duration : 0)
                    .catch(() => { /* circuit gone */ });
            }
        };
        const onEnded = function () {
            if (dotnet) dotnet.invokeMethodAsync('OnEnded').catch(() => { });
        };

        v.addEventListener('loadedmetadata', onLoaded);
        v.addEventListener('timeupdate', onTime);
        v.addEventListener('ended', onEnded);
        current = { video: v, dotnet: dotnet, onTime: onTime, onEnded: onEnded, onLoaded: onLoaded };

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
