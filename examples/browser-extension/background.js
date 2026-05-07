// AniSync auto-tracker background worker.
//
// Receives "watched" messages from content scripts, resolves the title to a
// concrete anime id via /api/v1/match, then writes progress to AniSync
// (which fans out to every linked provider). Keeps the API base URL + config
// UID out of every page's execution context — only this worker reads them.

const DEFAULT_API_BASE = 'https://anisync.fly.dev';
// Minimum match score below which we don't auto-write. 0.5 is generous enough
// to forgive year tags and dub/sub suffixes while still rejecting "Demon Slayer"
// → "Demon Slayer: Mugen Train" mismatches. Tune via the options page later.
const MIN_SCORE = 0.5;

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
  // Empty status keeps whatever the user already has on the entry —
  // AniSync's primary saves treat that as "preserve status, just update
  // progress" which is exactly the auto-track semantics we want.
  const body = {
    progress: episode,
    status: 'watching',
  };
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

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg?.type !== 'anisync:watched') return;

  (async () => {
    try {
      const { apiBase, uid } = await getConfig();
      if (!uid) {
        console.warn('[AniSync] no UID configured; open the extension options.');
        return;
      }

      const match = await findBestMatch(apiBase, msg.title);
      if (!match) {
        console.warn('[AniSync] no match for', msg.title);
        return;
      }
      if (match.score < MIN_SCORE) {
        console.warn(`[AniSync] match below threshold (${match.score}):`, msg.title, '→', match.name);
        return;
      }

      await saveProgress(apiBase, uid, match.id, msg.episode);
      console.log(`[AniSync] tracked ${match.name} ep ${msg.episode} (id ${match.id}, score ${match.score})`);
    } catch (e) {
      console.error('[AniSync] tracking failed:', e);
    }
  })();

  // Async work; tell Chrome we don't need to keep the message channel open.
  return false;
});
