// AniSync auto-tracker background worker.
//
// Receives "watched" messages from content scripts, resolves the title to a
// concrete anime id via /api/v1/match, then writes progress to AniSync
// (which fans out to every linked provider). Also handles the per-site
// opt-in flow: toolbar click injects content.js via activeTab, content
// script's "Always allow" button triggers chrome.permissions.request, on
// grant the site is registered as a dynamic content script so it
// auto-injects on future visits.

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
// Two surfaces:
//   1. Toolbar click on a non-pre-baked site — uses activeTab to inject
//      content.js into the current tab so the user gets a one-shot tracking
//      session. Doesn't grant any persistent permission.
//   2. "Always allow" button in the floating UI — content script messages
//      us; we call chrome.permissions.request from inside this handler so
//      the user gesture is still live (Chrome's permission dialog requires
//      that). On grant, register the origin as a dynamic content script so
//      future visits auto-inject without the toolbar dance.

function originFromUrl(rawUrl) {
  try {
    const u = new URL(rawUrl);
    // Build a match pattern compatible with both
    // chrome.permissions.request and chrome.scripting.registerContentScripts.
    // Use *:// so http and https both work — anime sites mix the two.
    return `*://${u.hostname}/*`;
  } catch {
    return null;
  }
}

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

// Toolbar button click: inject content.js into the current tab via activeTab.
// activeTab is a transient permission Chrome grants per-click, so this works
// even on sites we haven't been granted host permissions for. The injection
// is one-shot — closing the tab loses it. The "Always allow" CTA in the
// floating UI is what makes it permanent.
chrome.action.onClicked.addListener(async (tab) => {
  if (!tab?.id) return;
  try {
    await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      files: ['content.js'],
    });
  } catch (e) {
    // Restricted pages (chrome://, edge://, the Web Store) refuse injection.
    console.warn('[AniSync] injection refused on this page:', e.message);
  }
});

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

  if (msg.type === 'anisync:check-permission') {
    // Content script asks: "do I have a persistent permission on this host?"
    // Affects whether the floating UI shows the Always-allow CTA. Pre-baked
    // hosts (manifest static content_scripts) always return true.
    const url = sender.tab?.url || sender.url;
    const pattern = originFromUrl(url);
    if (!pattern) { sendResponse({ persistent: false }); return false; }
    chrome.permissions.contains({ origins: [pattern] }, (has) => {
      sendResponse({ persistent: has, pattern });
    });
    return true;
  }

  if (msg.type === 'anisync:request-permission') {
    // The floating UI's "Always allow" button. The user gesture from that
    // click propagates here through the message chain — Chrome will surface
    // its native permission dialog as long as we call permissions.request
    // synchronously inside this handler.
    const pattern = msg.pattern || originFromUrl(sender.tab?.url);
    if (!pattern) { sendResponse({ ok: false, error: 'no origin' }); return false; }

    chrome.permissions.request({ origins: [pattern] }, async (granted) => {
      if (!granted) {
        sendResponse({ ok: false, error: 'permission denied' });
        return;
      }
      try {
        await registerDynamicScript(pattern);
        await recordOptedIn(pattern);
        sendResponse({ ok: true, pattern });
      } catch (e) {
        console.error('[AniSync] register failed:', e);
        sendResponse({ ok: false, error: e.message ?? String(e) });
      }
    });
    return true;
  }

  return false;
});
