// AniSync auto-tracker background worker.
//
// Receives "watched" messages from content scripts, resolves the title to a
// concrete anime id via /api/v1/match, then writes progress to AniSync
// (which fans out to every linked provider). Keeps the API base URL + config
// UID out of every page's execution context — only this worker reads them.

const DEFAULT_API_BASE = 'https://anisync.fly.dev';
// Minimum match score below which we don't auto-write. 0.5 is generous enough
// to forgive year tags and dub/sub suffixes while still rejecting "Demon Slayer"
// → "Demon Slayer: Mugen Train" mismatches. Manual saves bypass this — if the
// user hand-types the title, we trust their judgement.
const MIN_AUTO_SCORE = 0.5;

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
  const url = `${apiBase}/api/v1/users/${uid}/entries/${encodeURIComponent(mediaId)}`;
  const body = { progress: episode, status: 'watching' };
  const r = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
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

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg?.type !== 'anisync:watched') return false;

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

  // Tell Chrome we're going to call sendResponse asynchronously.
  return true;
});
