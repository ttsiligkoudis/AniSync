// Media-type preference storage primitives (MULTI-SELECT), ported verbatim from
// the web app's wwwroot/js/media-type.js. Drives the anime / movies / series
// modes:
//   • the ENABLED SET — one or more modes the user picks in the chooser. The
//     dashboard combines shelves across them; Discover / Library only offer these.
//   • the ACTIVE mode — the single mode Discover / Library currently render.
// Choices are written to localStorage AND cookies (same keys as the web app) so
// the values stay in step across surfaces; there is no account/DB setting on this
// head. The open/close + selection state + reload live in MediaTypeModal.razor
// (the original's IIFE), but the persistence keys + cookie logic are identical.
const SET_KEY = 'anisync-media-types';   // enabled set (comma list)
const ACTIVE_KEY = 'anisync-media-type';  // active single
const SET_COOKIE = 'anisync_media_types';
const ACTIVE_COOKIE = 'anisync_media_type';
const ALL = ['anime', 'movie', 'series'];

function valid(v) { return ALL.indexOf(v) !== -1; }
function ordered(list) { return ALL.filter(function (t) { return list.indexOf(t) !== -1; }); }

function setCookie(name, v) {
    // URL-encode the value: the enabled set is comma-joined, and ASP.NET
    // Core's request-cookie parser treats a raw comma as a cookie separator
    // (it would read only the first mode). encodeURIComponent turns the
    // commas into %2C; the server URL-decodes on read.
    try { document.cookie = name + '=' + encodeURIComponent(v) + '; path=/; max-age=31536000; SameSite=Lax'; }
    catch (_) { /* cookies blocked — SSR falls back to anime */ }
}

// Ordered valid array from localStorage SET_KEY (or []).
export function getEnabled() {
    try {
        var raw = localStorage.getItem(SET_KEY);
        if (!raw) return [];
        return ordered(raw.split(',').filter(valid));
    } catch (_) { return []; }
}

// The stored active single mode (or null).
export function getActive() {
    try { return localStorage.getItem(ACTIVE_KEY); } catch (_) { return null; }
}

// Persist the full pick: enabled set (comma list) + active mode, to both
// localStorage and cookies, and drop the media-type-specific caches so the
// reload paints the new modes (mirrors the original confirm()).
export function save(setCsv, active) {
    try {
        localStorage.setItem(SET_KEY, setCsv);
        localStorage.setItem(ACTIVE_KEY, active);
    } catch (_) { /* private mode */ }
    setCookie(SET_COOKIE, setCsv);
    setCookie(ACTIVE_COOKIE, active);

    // Drop media-type-specific caches so the reload paints the new modes.
    try {
        localStorage.removeItem('anisync.continueWatching.v1');
        localStorage.removeItem('anisync.videoContinueWatching.v1');
    } catch (_) { /* ignore */ }
}

// The ACTIVE mode is owned by the Discover/Library toggle as well as the modal —
// persist just it (localStorage + cookie) so a switched mode survives a reload.
export function setActiveOnly(active) {
    try { localStorage.setItem(ACTIVE_KEY, active); } catch (_) { /* private mode */ }
    setCookie(ACTIVE_COOKIE, active);
}

// The dashboard's own toggle ("all" + the enabled modes) is tracked separately from the
// ACTIVE mode so the dashboard remembers its pick independently of Discover/Library. Client
// only (the dashboard filters its sections in-page), so localStorage is enough — no cookie.
const DASH_KEY = 'anisync-dash-filter';
export function getDashFilter() {
    try { return localStorage.getItem(DASH_KEY); } catch (_) { return null; }
}
export function setDashFilter(v) {
    try { localStorage.setItem(DASH_KEY, v); } catch (_) { /* private mode */ }
}

// Full-page reload after a pick — exactly what the original media-type.js did.
// Must be a RELOAD, not a Blazor Nav navigation: the app ships a cross-document
// @view-transition (navigation: auto), and a programmatic navigation fires it,
// then aborts ("Transition was skipped"), leaving the page blank. A reload is not
// a push/replace navigation, so it doesn't trigger the transition.
export function reload() { location.reload(); }

// Locks page scroll behind the chooser (the original added/removed this class).
export function setBodyOpen(on) {
    document.body.classList.toggle('mt-modal-open', !!on);
}

// Document-level Escape listener that calls back into .NET to close the modal —
// mirrors StreamsModal / configure.js. The dialog/backdrop visibility itself is
// Blazor state; this only owns the Esc key.
let _escHandler = null;

export function bindEscape(dotnet) {
    if (_escHandler) document.removeEventListener('keydown', _escHandler);
    _escHandler = function (e) {
        if (e.key === 'Escape' && dotnet) dotnet.invokeMethodAsync('CloseFromJs');
    };
    document.addEventListener('keydown', _escHandler);
}

export function unbindEscape() {
    if (_escHandler) {
        document.removeEventListener('keydown', _escHandler);
        _escHandler = null;
    }
}
