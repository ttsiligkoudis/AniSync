// Anime detail page interop — exact ports of the two inline <script> blocks in
// Views/Meta/Detail.cshtml that can't be expressed as Blazor @code:
//   1. The Stremio source chip's "open in native app, fall back to web" deep-link
//      probe (custom stremio:// protocol + visibilitychange/blur heuristic).
//   2. The trailer card's lazy YouTube-nocookie iframe swap.
// Synopsis collapse, episode collapse, season tabs, and episode-row navigation
// are handled natively in Detail.razor (Blazor state), matching the original
// behaviour without needing the DOM-toggle scripts.
//
// Both functions are idempotent per element (guarded by a data flag) so a
// Blazor re-render that re-invokes them after OnAfterRenderAsync doesn't double-
// bind handlers.

// ── Stremio source chip ──────────────────────────────────────────────────────
// Try the native app via the stremio:// custom protocol first, fall back to the
// web app when no handler claims it. Same pattern Slack / Discord / Spotify use:
// fire the protocol URL on the current tab, watch the page for visibilitychange /
// blur / pagehide; if the OS hands off to a registered Stremio handler within
// ~3s the page loses focus and we leave it alone, otherwise open the web URL in
// a new tab. The anchor's href stays the web fallback so right-click / middle-
// click / copy-link all resolve to a working URL without this handler.
export function initStremioChips(root) {
    var scope = root || document;
    scope.querySelectorAll('.source-chip-stremio[data-stremio-app-url]').forEach(function (chip) {
        if (chip.dataset.stremioBound) return;
        chip.dataset.stremioBound = '1';
        chip.addEventListener('click', function (e) {
            var appUrl = this.dataset.stremioAppUrl;
            var webUrl = this.href;
            if (!appUrl) return;
            // Defer to default behaviour for modifier-click / middle-click — those
            // are explicit "open in new tab" gestures and shouldn't be swallowed.
            if (e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || e.button === 1) return;

            e.preventDefault();

            // Loading state on the chip so the ~3s app-probe window doesn't read
            // as a frozen click. Stores the original markup so it can be restored
            // after the probe resolves.
            if (!chip.dataset.originalContent) {
                chip.dataset.originalContent = chip.innerHTML;
            }
            chip.classList.add('is-loading');
            chip.innerHTML = '<span class="source-chip-mark" aria-hidden="true">' +
                '<span class="source-chip-spinner" role="status" aria-label="Opening…"></span>' +
                '</span><span class="source-chip-label">Opening…</span>';

            // App-handoff signal. The browser tab going `hidden` is a reliable cue
            // on mobile; on desktop `blur` fires when focus moves to the new app
            // window. We listen for both and discount signals within 250ms of the
            // click since those are almost always synthetic (browser-internal
            // focus shifts from the protocol navigation, not the OS app handoff).
            var appClaimed = false;
            var clickTime = Date.now();
            var claim = function () {
                if (Date.now() - clickTime < 250) return;
                appClaimed = true;
            };
            document.addEventListener('visibilitychange', claim);
            window.addEventListener('blur', claim);
            window.addEventListener('pagehide', claim);

            // Trigger the stremio:// scheme on the current tab, deferred past the
            // next paint so the spinner can render before the browser's "Allow
            // this site to open stremio?" prompt pauses page animations.
            setTimeout(function () {
                try { window.location.href = appUrl; } catch (_) { }
            }, 250);

            // 3000ms is long enough for the user to see + dismiss an "Allow this
            // site to open stremio?" prompt before the no-app fallback fires.
            setTimeout(function () {
                document.removeEventListener('visibilitychange', claim);
                window.removeEventListener('blur', claim);
                window.removeEventListener('pagehide', claim);

                chip.classList.remove('is-loading');
                if (chip.dataset.originalContent) {
                    chip.innerHTML = chip.dataset.originalContent;
                    delete chip.dataset.originalContent;
                }

                if (appClaimed || document.hidden) return;

                // No app handler claimed the deep link in time — open the web
                // fallback in a new tab. `noopener` is intentionally omitted so a
                // popup-blocked window is detectable; we never navigate the
                // original tab (the click should never disturb the page).
                try { window.open(webUrl, '_blank'); }
                catch (_) { /* popup-blocked — silent no-op */ }
            }, 3000);
        });
    });
}

// ── Trailer lazy-embed ────────────────────────────────────────────────────────
// Click → swap the thumbnail for a youtube-nocookie iframe (privacy-light embed
// domain; no cookies until playback). The embed itself is ~500 KB–1 MB so we
// never load it until the user opts in. When embedding is disabled / blocked,
// YouTube renders its own in-frame "Watch on YouTube" error page, so we don't
// build a fallback ourselves.
export function initTrailer(root) {
    var scope = root || document;
    scope.querySelectorAll('.anime-detail-trailer-card[data-yt-id]').forEach(function (card) {
        if (card.dataset.trailerBound) return;
        card.dataset.trailerBound = '1';
        card.addEventListener('click', function (e) {
            e.preventDefault();
            var ytId = card.getAttribute('data-yt-id');
            if (!ytId) return;
            // Idempotent: a re-click on a card already swapped to an iframe is a
            // no-op (the iframe keeps playing).
            if (card.querySelector('iframe')) return;

            var iframe = document.createElement('iframe');
            iframe.className = 'anime-detail-trailer-iframe';
            iframe.allow = 'accelerometer; autoplay; encrypted-media; gyroscope; picture-in-picture; fullscreen';
            // The page-level no-referrer policy makes YouTube's embed serve
            // "error 153"; 'origin' sends just "https://this-host/" (no path /
            // query) which satisfies YouTube's domain check without leaking the
            // config UID from the URL.
            iframe.referrerPolicy = 'origin';
            iframe.setAttribute('allowfullscreen', '');
            iframe.title = 'Trailer';
            iframe.src = 'https://www.youtube-nocookie.com/embed/' + encodeURIComponent(ytId)
                + '?autoplay=1&rel=0&modestbranding=1';

            card.innerHTML = '';
            card.appendChild(iframe);
            card.classList.add('anime-detail-trailer-playing');
            // No longer a "go to YouTube" link — neutralise the anchor semantics
            // (keeping the <a> avoids re-laying out the section).
            card.removeAttribute('href');
            card.removeAttribute('target');
            card.style.cursor = 'default';
        });
    });
}

// ── Watch-entry marker ───────────────────────────────────────────────────────
// Stash sessionStorage['aniSyncWatchEntry']='fromAnime' on the click that
// navigates from the detail page to /watch, so the Watch page's smart back-link
// knows the detail page is the previous history entry and can reuse it with a
// plain history.back() (avoiding a duplicate detail entry on the stack). Ported
// from the inline <script> in Views/Meta/Detail.cshtml. Needed because the
// site's no-referrer policy strips document.referrer on same-origin navs, so the
// explicit marker is the only reliable signal. Modifier / middle clicks (open in
// new tab) are skipped. Bound once per document (delegated, idempotent).
var _watchEntryBound = false;
export function initWatchEntryMarker() {
    if (_watchEntryBound) return;
    _watchEntryBound = true;
    document.addEventListener('click', function (e) {
        if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        var a = e.target.closest && e.target.closest('a[href*="/watch/"]');
        if (!a) return;
        try { sessionStorage.setItem('aniSyncWatchEntry', 'fromAnime'); }
        catch (_) { /* sessionStorage unavailable — best effort */ }
    }, true);
}

// Convenience: bind everything on the detail page in one interop call.
export function init(root) {
    initStremioChips(root);
    initTrailer(root);
    initWatchEntryMarker();
}
