// AniSync auto-tracker background worker.
//
// Receives "watched" messages from content scripts, resolves the title to a
// concrete anime id via /api/v1/match, then writes progress to AniSync
// (which fans out to every linked provider). Also handles the per-site
// opt-in flow: the toolbar popup (popup.html / popup.js) calls
// chrome.permissions.request directly — popup is the only context that works
// for this on both Chrome and Firefox — and on grant messages us to register
// a dynamic content script so the site auto-injects on future visits.

const DEFAULT_API_BASE = 'https://anisync.fly.dev';
// Minimum match score below which we don't auto-write. 0.5 is generous enough
// to forgive year tags and dub/sub suffixes while still rejecting "Demon Slayer"
// → "Demon Slayer: Mugen Train" mismatches. Manual saves bypass this — if the
// user hand-types the title, we trust their judgement.
const MIN_AUTO_SCORE = 0.5;
// Shape that chrome.scripting.registerContentScripts ids must match — we
// derive the id from the origin so a single origin can be re-registered
// without duplicates after extension updates.
const SCRIPT_ID_PREFIX = 'site-';

async function getConfig() {
  const stored = await chrome.storage.local.get(['apiBase', 'uid']);
  return {
    apiBase: stored.apiBase || DEFAULT_API_BASE,
    uid: stored.uid || null,
  };
}

async function findBestMatch(apiBase, title) {
  const url = `${apiBase}/api/v1/match?title=${encodeURIComponent(title)}&limit=1`;
  const r = await fetch(url);
  if (!r.ok) throw new Error(`match: ${r.status}`);
  const body = await r.json();
  return body.matches?.[0] || null;
}

async function saveProgress(apiBase, uid, mediaId, episode) {
  // /api/v1/me/* — UID rides in the X-AniSync-Config header, never in the URL,
  // so it can't leak through reverse-proxy / CDN access logs or browser
  // history. Only the Stremio addon endpoints carry it in the path because
  // their addon-protocol shape leaves us no choice there.
  const url = `${apiBase}/api/v1/me/entries/${encodeURIComponent(mediaId)}`;
  const body = { progress: episode, status: 'watching' };
  const r = await fetch(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-AniSync-Config': uid,
    },
    body: JSON.stringify(body),
  });
  if (!r.ok) {
    const text = await r.text().catch(() => '');
    throw new Error(`save: ${r.status} ${text}`);
  }
  return r.json();
}

// Returns a structured result so the floating UI can show "Saved" or the
// specific error. The auto-fire path also funnels through here but ignores
// the response — only the manual-confirm button cares.
async function processWatched(msg) {
  const { apiBase, uid } = await getConfig();
  if (!uid) {
    return { ok: false, error: 'no UID configured — open the extension options' };
  }

  const match = await findBestMatch(apiBase, msg.title);
  if (!match) {
    return { ok: false, error: `no match for "${msg.title}"` };
  }
  if (!msg.manual && match.score < MIN_AUTO_SCORE) {
    return { ok: false, error: `match below threshold (${match.score})`, name: match.name };
  }

  await saveProgress(apiBase, uid, match.id, msg.episode);
  return { ok: true, name: match.name, id: match.id, score: match.score, episode: msg.episode };
}

// ── Per-site opt-in flow ────────────────────────────────────────────────
//
// User clicks AniSync toolbar icon → popup.html opens → popup calls
// chrome.permissions.request itself (popups are the only UI surface where
// this reliably works in both Chrome and Firefox) → on grant, popup also
// injects content.js into the active tab AND messages us with
// 'anisync:register-site' so we can register a persistent dynamic content
// script for the origin. We rehydrate the dynamic registry on every extension
// startup because Chrome wipes it across updates.

function scriptIdForPattern(pattern) {
  return SCRIPT_ID_PREFIX + pattern.replace(/[^a-z0-9]/gi, '_');
}

async function recordOptedIn(pattern) {
  const { optedInOrigins = [] } = await chrome.storage.local.get('optedInOrigins');
  if (!optedInOrigins.includes(pattern)) {
    optedInOrigins.push(pattern);
    await chrome.storage.local.set({ optedInOrigins });
  }
}

async function registerDynamicScript(pattern) {
  const id = scriptIdForPattern(pattern);
  // Skip if a script with this id is already registered — registerContentScripts
  // throws on duplicate ids.
  const existing = await chrome.scripting.getRegisteredContentScripts({ ids: [id] }).catch(() => []);
  if (existing.length > 0) return;
  await chrome.scripting.registerContentScripts([{
    id,
    matches: [pattern],
    js: ['content.js'],
    runAt: 'document_idle',
    allFrames: false,
  }]);
}

// Re-register dynamic scripts for opted-in origins on every extension start.
// Chrome persists registered scripts across browser restarts, but extension
// updates wipe the dynamic registry — without this rehydrate step, users
// would silently lose tracking on sites they'd opted into.
async function syncDynamicScripts() {
  const { optedInOrigins = [] } = await chrome.storage.local.get('optedInOrigins');
  if (optedInOrigins.length === 0) return;

  const allPerms = await chrome.permissions.getAll();
  const granted = new Set(allPerms.origins || []);
  const stillGranted = optedInOrigins.filter(p => granted.has(p));

  // Drop any storage entries the user revoked via Chrome's permission UI.
  if (stillGranted.length !== optedInOrigins.length) {
    await chrome.storage.local.set({ optedInOrigins: stillGranted });
  }

  for (const pattern of stillGranted) {
    try { await registerDynamicScript(pattern); }
    catch (e) { console.warn('[AniSync] register failed for', pattern, e.message); }
  }
}

chrome.runtime.onStartup.addListener(syncDynamicScripts);
chrome.runtime.onInstalled.addListener(syncDynamicScripts);

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg?.type) return false;

  if (msg.type === 'anisync:watched') {
    processWatched(msg)
      .then((result) => {
        if (result.ok) {
          console.log(`[AniSync] tracked ${result.name} ep ${result.episode} (id ${result.id}, score ${result.score})`);
        } else {
          console.warn('[AniSync] tracking skipped:', result.error, msg.title);
        }
        sendResponse(result);
      })
      .catch((e) => {
        console.error('[AniSync] tracking failed:', e);
        sendResponse({ ok: false, error: e.message ?? String(e) });
      });
    return true;
  }

  if (msg.type === 'anisync:register-site') {
    // Popup has just been granted a host permission and wants us to make
    // the injection persistent. We DON'T call permissions.request here —
    // the popup already did, and only the popup's frame has the user gesture
    // Firefox needs. We just register the dynamic script and remember it.
    const pattern = msg.pattern;
    if (!pattern) { sendResponse({ ok: false, error: 'no pattern' }); return false; }

    (async () => {
      try {
        // Defensive: confirm we still hold the permission. If the user denied
        // and somehow this message arrived anyway, abort cleanly.
        const has = await chrome.permissions.contains({ origins: [pattern] });
        if (!has) {
          sendResponse({ ok: false, error: 'permission not granted' });
          return;
        }
        await registerDynamicScript(pattern);
        await recordOptedIn(pattern);
        sendResponse({ ok: true, pattern });
      } catch (e) {
        console.error('[AniSync] register failed:', e);
        sendResponse({ ok: false, error: e.message ?? String(e) });
      }
    })();
    return true;
  }

  return false;
});
