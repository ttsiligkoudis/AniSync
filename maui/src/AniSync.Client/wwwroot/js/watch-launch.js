// Watch page interop — stage-3a player-glue browser-only concerns that can't
// live in Blazor @code (DOM / OS handoff):
//
//   1. The external-player launcher (openWithExternalPlayer + dispatchExternalLaunch
//      + downloadM3uForStream), ported verbatim from Views/Meta/Watch.cshtml. One
//      platform path each:
//        Android — intent:// with action.VIEW + type=video/* (no package=) so the
//          system app-chooser appears; VLC's subtitles_location extra carries the
//          sidecar VTT.
//        Desktop / iOS — probe vlc://<url> via a hidden iframe; if the tab doesn't
//          lose focus within ~1.2s the handler isn't registered, so fall back to a
//          .m3u Blob download with an #EXTVLCOPT:input-slave sidecar line.
//        iOS Safari — after the .m3u download, surface the Files → Share → Open in…
//          toast (the original gated this on window.AniSyncToast; here the message
//          is handed back to .NET which renders the notice — see Watch.razor).
//      resolvePlaybackUrl + the sidecar VTT URL are resolved in C# (AniSyncApi)
//      and passed in, so this module only does the browser dispatch.
//
//   2. paintSkipRanges — translucent OP / ED bands over the player's progress
//      strip. The original painted onto ArtPlayer's .art-control-progress; the
//      stage-3a web head still uses a bare <video controls> (the ArtPlayer engine
//      swap is the deferred stage 3b), whose native progress bar lives in the
//      browser's shadow DOM and can't be overlaid. So we paint the bands onto a
//      thin overlay strip pinned to the bottom edge of the #watch-video frame —
//      same seconds→% math, same .watch-skip-range class + tooltip, visible at a
//      glance. When stage 3b lands and a custom progress bar exists, this retargets
//      to it.

// ── External-player launcher ─────────────────────────────────────────────────

// Cross-platform "Open with…" dispatch. streamUrl is the already-resolved debrid
// CDN URL (C# ran AniSyncApi.ResolveStreamAsync); sidecarSub, when set, is an
// absolute /api/v1/sub/{b64}/subtitle.vtt URL (built in C# so it targets the API
// origin). Returns a string when the caller should surface a toast (iOS Files
// hint), else null — keeps the toast policy in .NET (no global toast in the
// replica).
export function openExternally(streamUrl, displayName, sidecarSub) {
    if (!streamUrl) return null;
    var ua = '';
    try { ua = navigator.userAgent || ''; } catch (_) { }

    // Android: native app chooser via intent://.
    if (/Android/i.test(ua) && !/Windows Phone/i.test(ua)) {
        var u;
        try { u = new URL(streamUrl); }
        catch (_) { try { window.open(streamUrl, '_blank'); } catch (e) { } return null; }
        var scheme = u.protocol.replace(':', '');
        var rest = u.host + u.pathname + u.search + u.hash;
        var title = displayName || 'AniSync stream';
        // VLC for Android reads the subtitle URL from the subtitles_location
        // extra (snake_case). Other players ignore it harmlessly.
        var subExtra = sidecarSub
            ? ';S.subtitles_location=' + encodeURIComponent(sidecarSub)
            : '';
        var intent = 'intent://' + rest +
            '#Intent' +
            ';scheme=' + scheme +
            ';action=android.intent.action.VIEW' +
            ';type=video/*' +
            ';S.title=' + encodeURIComponent(title) +
            subExtra +
            ';end';
        try { window.location.href = intent; } catch (_) { }
        return null;
    }

    // Desktop / iOS: probe vlc:// first, fall back to .m3u download.
    var launched = false;
    function markLaunched() { launched = true; }
    // Either signal is enough — an external app taking over blurs the tab /
    // hides the page. { once: true } so no cleanup is needed.
    try { window.addEventListener('blur', markLaunched, { once: true }); } catch (_) { }
    var vh = function () {
        if (document.visibilityState === 'hidden') {
            markLaunched();
            try { document.removeEventListener('visibilitychange', vh); } catch (_) { }
        }
    };
    try { document.addEventListener('visibilitychange', vh); } catch (_) { }

    // Hidden iframe rather than location.href: the unregistered-protocol
    // failure mode is silent (no browser popup to dismiss). Registered
    // handlers fire just the same.
    var probe = document.createElement('iframe');
    probe.style.display = 'none';
    probe.src = 'vlc://' + streamUrl;
    document.body.appendChild(probe);

    var iosToast = isIosSafari()
        ? 'Downloaded. In Files, long-press the .m3u → Share → Open in… → VLC / Infuse.'
        : null;

    setTimeout(function () {
        try { document.body.removeChild(probe); } catch (_) { }
        if (launched) return;
        // Handler wasn't registered (or didn't dispatch) — fall back to the
        // .m3u download. Whichever player owns the .m3u association opens the
        // stream; the sidecar is already a /subtitle.vtt path so VLC's
        // extension sniff registers it as a subtitle slave.
        downloadM3u(streamUrl, displayName, sidecarSub);
    }, 1200);

    // Return the iOS toast text up front so .NET can schedule it; the download
    // fires asynchronously above but the message applies the moment it lands.
    return iosToast;
}

function downloadM3u(streamUrl, displayName, sidecarSub) {
    var safeName = (displayName || 'stream')
        .replace(/[\\/:*?"<>|]/g, '_')
        .replace(/\s+/g, ' ')
        .trim()
        .substring(0, 80) || 'stream';

    // input-slave=URI loads the URI as an auxiliary input; for a .vtt URL VLC's
    // demuxer recognises it as a subtitle slave. The EXTVLCOPT line must sit
    // BETWEEN the EXTINF marker and the URL it describes.
    var subLine = sidecarSub
        ? '#EXTVLCOPT:input-slave=' + sidecarSub + '\n'
        : '';
    var m3u =
        '#EXTM3U\n' +
        '#EXTINF:-1,' + safeName + '\n' +
        subLine +
        streamUrl + '\n';
    try { console.info('[AniSync] external-launch m3u:\n' + m3u); } catch (_) { }
    var blob = new Blob([m3u], { type: 'audio/x-mpegurl' });
    var blobUrl = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = blobUrl;
    a.download = safeName + '.m3u';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    // Revoke after the click flushes — some browsers cancel the download if the
    // URL is revoked synchronously.
    setTimeout(function () {
        try { URL.revokeObjectURL(blobUrl); } catch (_) { }
    }, 5000);
}

function isIosSafari() {
    var ua = '';
    try { ua = navigator.userAgent || ''; } catch (_) { return false; }
    if (/iPad|iPhone|iPod/.test(ua)) return true;
    if (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1) return true;
    return false;
}

// ── AniSkip progress-bar paint ───────────────────────────────────────────────

// Paint translucent OP / ED bands over the player's progress strip. `intro` /
// `outro` are { start, end } in seconds (or null); `dur` is the video duration.
// Idempotent — clears prior bands before re-painting, so it's safe to call on
// every loadedmetadata / progress tick. Returns true once a band was painted (so
// the caller can stop retrying for an unknown-duration video).
export function paintSkipRanges(videoId, intro, outro, dur) {
    var video = document.getElementById(videoId);
    if (!video) return false;
    if (!intro && !outro) { clearSkipRanges(videoId); return true; }

    // Prefer a custom progress bar if a future stage-3b ArtPlayer swap added one;
    // otherwise overlay a thin strip on the bottom edge of the <video> frame (the
    // native controls' progress bar isn't reachable in the shadow DOM).
    var frame = video.parentElement || video;
    var host = frame.querySelector('.art-control-progress');
    var ownStrip = false;
    if (!host) {
        host = frame.querySelector('.watch-skip-strip');
        if (!host) {
            // Only create the overlay strip when the frame can position it.
            try {
                var cs = window.getComputedStyle(frame);
                if (cs.position === 'static') frame.style.position = 'relative';
            } catch (_) { }
            host = document.createElement('div');
            host.className = 'watch-skip-strip';
            // Thin strip pinned to the bottom edge, above the native controls'
            // own bar so the bands read as upcoming skip points. Pointer-events
            // off so the native scrubber keeps hover/seek.
            host.style.position = 'absolute';
            host.style.left = '0';
            host.style.right = '0';
            host.style.bottom = '0';
            host.style.height = '4px';
            host.style.pointerEvents = 'none';
            host.style.zIndex = '2';
            frame.appendChild(host);
        }
        ownStrip = true;
    }

    if (!dur || !isFinite(dur)) {
        dur = (video && isFinite(video.duration)) ? video.duration : 0;
    }
    if (!dur || !isFinite(dur)) return false;

    // For the ArtPlayer host, copy the played-bar's vertical position so the
    // band lines up with the rendered bar (the original's getBoundingClientRect
    // approach). The own-strip case is already the right height/position.
    var refTop = null, refHeight = null;
    if (!ownStrip) {
        var ref = host.querySelector('.art-progress-played') || host.querySelector('.art-progress-loaded');
        if (ref) {
            var hostRect = host.getBoundingClientRect();
            var refRect = ref.getBoundingClientRect();
            refTop = refRect.top - hostRect.top;
            refHeight = refRect.height;
        }
    }

    // Idempotent: nuke prior fills before re-paint.
    var prior = host.querySelectorAll('.watch-skip-range');
    for (var i = 0; i < prior.length; i++) prior[i].remove();

    function addBand(range, kind) {
        if (!range) return;
        var startPct = Math.max(0, Math.min(100, (range.start / dur) * 100));
        var endPct = Math.max(0, Math.min(100, (range.end / dur) * 100));
        var width = Math.max(0, endPct - startPct);
        if (width <= 0) return;
        var band = document.createElement('span');
        band.className = 'watch-skip-range';
        band.style.left = startPct + '%';
        band.style.width = width + '%';
        if (refTop != null) {
            band.style.top = refTop + 'px';
            band.style.height = refHeight + 'px';
        } else if (ownStrip) {
            band.style.top = '0';
            band.style.height = '100%';
        }
        band.title = (kind === 'intro' ? 'Opening' : 'Ending')
            + ' — ' + Math.round(range.start) + 's → ' + Math.round(range.end) + 's';
        host.appendChild(band);
    }
    addBand(intro, 'intro');
    addBand(outro, 'outro');
    return true;
}

export function clearSkipRanges(videoId) {
    var video = document.getElementById(videoId);
    if (!video) return;
    var frame = video.parentElement || video;
    var strips = frame.querySelectorAll('.watch-skip-strip, .art-control-progress');
    strips.forEach(function (host) {
        var bands = host.querySelectorAll('.watch-skip-range');
        bands.forEach(function (b) { b.remove(); });
        if (host.classList.contains('watch-skip-strip')) {
            try { host.remove(); } catch (_) { }
        }
    });
}

// ── History-stack + swipe nav ────────────────────────────────────────────────
// Ported from the two inline <script> blocks in Views/Meta/Watch.cshtml:
//   - wireNav: classify how we reached /watch on sessionStorage, then bind the
//     prev/next links (location.replace → flat back stack) and the "← {title}"
//     back-link (history.back() when the detail page is right behind, else
//     location.replace to swap /watch out in place).
//   - wireSwipe: a horizontal swipe on the page → prev/next episode.
// Blazor renders the <a href>s; these wire the click/touch behaviour on top,
// reading the hrefs straight off the rendered anchors.

var ENTRY_KEY = 'aniSyncWatchEntry';
var _navBound = false;

// Wire the history-stack hygiene once per /watch load. Idempotent (guarded) so a
// Blazor re-render re-invoking it never double-binds. Verbatim port of the inline
// IIFE in Views/Meta/Watch.cshtml, reading hrefs straight off the rendered <a>s:
//   - classify the entry (fromOther unless Detail set fromAnime);
//   - prev/next links → location.replace (keeps the back stack flat);
//   - back-link → history.back() when detail is right behind (fromAnime), else
//     location.replace to swap /watch out in place.
// Modifier / middle clicks fall through to the default <a> (open in new tab).
export function wireNav() {
    // Classify how we got here once on load.
    try {
        if (!sessionStorage.getItem(ENTRY_KEY)) {
            sessionStorage.setItem(ENTRY_KEY, 'fromOther');
        }
    } catch (e) { /* sessionStorage unavailable — best effort */ }

    if (_navBound) return;
    _navBound = true;

    // Replace, don't push, between adjacent episodes.
    var navLinks = document.querySelectorAll('.watch-nav-prev, .watch-nav-next');
    for (var i = 0; i < navLinks.length; i++) {
        navLinks[i].addEventListener('click', function (e) {
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            e.preventDefault();
            try { window.location.replace(this.href); }
            catch (_) { window.location.href = this.href; }
        });
    }

    // Back-link: always lands on the detail page.
    var back = document.querySelector('.watch-back');
    if (back) {
        back.addEventListener('click', function (e) {
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            e.preventDefault();
            var entry;
            try { entry = sessionStorage.getItem(ENTRY_KEY); } catch (_) { entry = null; }
            try { sessionStorage.removeItem(ENTRY_KEY); } catch (_) { }
            if (entry === 'fromAnime' && window.history.length > 1) {
                window.history.back();
            } else {
                try { window.location.replace(back.href); }
                catch (_) { window.location.href = back.href; }
            }
        });
    }
}

// Episode swipe-nav — verbatim port of the inline IIFE in Views/Meta/Watch.cshtml.
// A horizontal swipe ON THE PAGE (document-level) navigates to the prev/next
// episode, reusing the nav buttons' own rendered hrefs. Touches that start inside
// the player (#watch-video's frame), the source list, the toolbar, or any
// interactive control are ignored so the player's native gestures (seek/volume)
// and stream taps are never hijacked. Swipe left → next, right → prev. Uses a
// normal navigation (push) like the original — the flat-stack replace logic is
// the prev/next BUTTONS' behaviour, not the swipe's. Idempotent (detaches first).
var _swipeHandlers = null;
export function wireSwipe() {
    unwireSwipe();
    var prev = document.querySelector('.watch-nav-prev');
    var next = document.querySelector('.watch-nav-next');
    if (!prev && !next) return;
    // The replica's player frame is .watch-player (the original's #watchPlayer).
    var player = document.querySelector('.watch-player');
    var startX = 0, startY = 0, tracking = false;
    var H_THRESHOLD = 70;  // min horizontal travel to count as a swipe
    var V_LIMIT = 50;      // max vertical drift to stay "horizontal"

    var onStart = function (e) {
        if (!e.touches || e.touches.length !== 1) { tracking = false; return; }
        var t = e.target;
        if (player && player.contains(t)) { tracking = false; return; }
        if (t.closest && t.closest('a, button, input, textarea, select, [role="slider"], .watch-sources, .watch-player-toolbar, .watch-player-fallback')) {
            tracking = false;
            return;
        }
        startX = e.touches[0].clientX;
        startY = e.touches[0].clientY;
        tracking = true;
    };
    var onEnd = function (e) {
        if (!tracking) return;
        tracking = false;
        var touch = e.changedTouches && e.changedTouches[0];
        if (!touch) return;
        var dx = touch.clientX - startX;
        var dy = touch.clientY - startY;
        if (Math.abs(dx) < H_THRESHOLD || Math.abs(dy) > V_LIMIT) return;
        var target = dx < 0 ? next : prev;
        if (target && target.href) {
            try { if (window.AniSyncHaptics) window.AniSyncHaptics.tick(); } catch (_) { }
            try { window.location.href = target.href; } catch (_) { }
        }
    };
    document.addEventListener('touchstart', onStart, { passive: true });
    document.addEventListener('touchend', onEnd, { passive: true });
    _swipeHandlers = { onStart: onStart, onEnd: onEnd };
}

export function unwireSwipe() {
    if (_swipeHandlers) {
        try {
            document.removeEventListener('touchstart', _swipeHandlers.onStart);
            document.removeEventListener('touchend', _swipeHandlers.onEnd);
        } catch (_) { }
    }
    _swipeHandlers = null;
}
