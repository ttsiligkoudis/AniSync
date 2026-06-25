// Custom horizontal drag-scroll for .poster-scroll-row shelves.
//
// The WebView's native fling overshoots: a quick flick scrolls many tiles no matter how far the thumb
// actually travelled. Here we drive the scroll ourselves — scrollLeft tracks the finger 1:1 during the
// drag, and release adds only a short, capped, friction-decayed glide proportional to the RECENT drag
// velocity. So distance scrolled follows distance dragged (+ a little for speed), and a light flick
// can't run away. Vertical-dominant gestures fall through to the page; taps still open the tile.
//
// touch-action: pan-y (set on enable) tells the browser to keep handling vertical page-scroll natively
// while leaving the horizontal axis to us, so we never fight the native horizontal fling. It's set from
// JS (not CSS) so that if this module ever fails to load, native horizontal scrolling still works.

const ENABLED = new WeakSet();

// Tuning — all easy to dial. Lower MAX_V / FRICTION = shorter, calmer fling.
const TAP_SLOP = 8;      // px; below this the gesture is a tap, not a drag (tile opens)
const MAX_V = 1.5;       // px/ms cap on release velocity — the main "don't run away" knob
const FRICTION = 0.90;   // per-16ms velocity decay during the glide (lower = stops sooner)
const MIN_V = 0.02;      // px/ms; the glide ends below this

export function enable(row) {
    if (!row || ENABLED.has(row)) return;
    ENABLED.add(row);
    // Hand the browser vertical panning, keep horizontal for us (see header note).
    try { row.style.touchAction = 'pan-y'; } catch (_) { }

    let active = false;    // a single touch is down on the row
    let dragging = false;  // confirmed horizontal drag — we own the scroll
    let decided = false;   // axis lock resolved for this gesture
    let startX = 0, startY = 0, startLeft = 0, lastX = 0, lastT = 0, vel = 0;
    let raf = 0;

    const stopGlide = function () { if (raf) { cancelAnimationFrame(raf); raf = 0; } };

    row.addEventListener('touchstart', function (e) {
        if (!e.touches || e.touches.length !== 1) { active = false; return; }
        stopGlide();
        active = true; dragging = false; decided = false;
        const t = e.touches[0];
        startX = lastX = t.clientX; startY = t.clientY;
        startLeft = row.scrollLeft; lastT = e.timeStamp; vel = 0;
    }, { passive: true });

    row.addEventListener('touchmove', function (e) {
        if (!active || !e.touches || e.touches.length !== 1) return;
        const t = e.touches[0];
        const dx = t.clientX - startX;
        const dy = t.clientY - startY;
        if (!decided) {
            if (Math.abs(dx) < TAP_SLOP && Math.abs(dy) < TAP_SLOP) return; // still a tap
            decided = true;
            dragging = Math.abs(dx) > Math.abs(dy); // horizontal → ours; vertical → leave to the page
            if (!dragging) { active = false; return; }
        }
        if (!dragging) return;
        // 1:1 finger tracking (no preventDefault needed — touch-action: pan-y already kept the browser
        // off the horizontal axis, so this is the only thing moving the row).
        row.scrollLeft = startLeft - dx;
        const now = e.timeStamp;
        const dt = now - lastT;
        if (dt > 0) {
            const inst = (t.clientX - lastX) / dt; // px/ms, + = finger moving right
            vel = vel * 0.7 + inst * 0.3;          // light smoothing
            lastX = t.clientX; lastT = now;
        }
    }, { passive: true });

    const end = function () {
        if (!active) return;
        active = false;
        if (!dragging) return;
        dragging = false;
        // A drag just happened — swallow the click the browser fires next so the tile under the finger
        // doesn't open / navigate.
        suppressNextClick(row);
        // scrollLeft moves opposite to the finger, so the glide velocity is -fingerVelocity, capped.
        let v = -vel;
        if (v > MAX_V) v = MAX_V; else if (v < -MAX_V) v = -MAX_V;
        if (Math.abs(v) < MIN_V) return;
        let lastTs = 0;
        const step = function (ts) {
            if (!lastTs) lastTs = ts;
            const dt = Math.min(32, ts - lastTs);
            lastTs = ts;
            row.scrollLeft += v * dt;
            v *= Math.pow(FRICTION, dt / 16);
            if (Math.abs(v) < MIN_V) { raf = 0; return; }
            raf = requestAnimationFrame(step);
        };
        raf = requestAnimationFrame(step);
    };
    row.addEventListener('touchend', end, { passive: true });
    row.addEventListener('touchcancel', function () { active = false; dragging = false; }, { passive: true });
}

// One-shot capture-phase click swallow: stops the post-drag click from reaching the tile's <a>
// (href nav) or Blazor's delegated handler. Cleared on the first click or after a short timeout.
function suppressNextClick(row) {
    const onClick = function (ev) { ev.stopPropagation(); ev.preventDefault(); cleanup(); };
    const cleanup = function () { try { row.removeEventListener('click', onClick, true); } catch (_) { } };
    row.addEventListener('click', onClick, true);
    setTimeout(cleanup, 400);
}
