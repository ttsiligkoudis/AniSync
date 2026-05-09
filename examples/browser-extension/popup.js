// Toolbar popup — the cross-browser permission gateway.
//
// Why this is a popup and not in-page UI:
//   chrome.permissions.request can only fire its native permission dialog
//   when called from inside a real user-input handler. Chrome propagates the
//   gesture through runtime.sendMessage chains so a content-script click can
//   reach into the background worker, but Firefox does not — it requires the
//   call to live in the same JS frame as the click. A popup is the smallest
//   surface that satisfies both browsers identically. One code path.

const $host = document.getElementById('host');
const $trackBtn = document.getElementById('track-once');
const $allowBtn = document.getElementById('always-allow');
const $allowDesc = $allowBtn.nextElementSibling;
const $trackDesc = document.getElementById('track-desc');
const $status = document.getElementById('status');
const $badge = document.getElementById('granted-badge');
const $version = document.getElementById('version');
const $openOptions = document.getElementById('open-options');

function setStatus(text, kind) {
  $status.textContent = text || '';
  $status.className = 'status' + (kind ? ' ' + kind : '');
}

function isInjectableUrl(url) {
  // Privileged schemes refuse content-script injection on every browser.
  // Chrome blocks chrome://, edge://, the Web Store, and view-source:.
  // Firefox blocks about:, moz-extension://, and addons.mozilla.org.
  return /^https?:\/\//i.test(url || '');
}

async function getActiveTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab || null;
}

async function refreshUi(tab) {
  if (!tab || !isInjectableUrl(tab.url)) {
    $host.textContent = 'Not available on this page.';
    $trackBtn.disabled = true;
    $allowBtn.disabled = true;
    return null;
  }

  const url = new URL(tab.url);
  const pattern = `*://${url.hostname}/*`;
  $host.textContent = url.hostname;

  let alreadyGranted = false;
  try {
    alreadyGranted = await chrome.permissions.contains({ origins: [pattern] });
  } catch {
    // Some restricted patterns throw on .contains — treat as not-granted.
  }

  if (alreadyGranted) {
    $badge.hidden = false;
    $allowBtn.hidden = true;
    $allowDesc.hidden = true;
    $trackDesc.textContent = 'AniSync auto-runs here. Re-inject if the page just loaded and the tracker is missing.';
  }

  return { tab, pattern, alreadyGranted };
}

async function injectContentScript(tabId) {
  await chrome.scripting.executeScript({
    target: { tabId },
    files: ['content.js'],
  });
}

$trackBtn.addEventListener('click', async () => {
  const tab = await getActiveTab();
  if (!tab) return;
  $trackBtn.disabled = true;
  try {
    await injectContentScript(tab.id);
    setStatus('Tracker injected.', 'ok');
    setTimeout(() => window.close(), 350);
  } catch (e) {
    setStatus('Inject failed: ' + (e?.message || e), 'error');
    $trackBtn.disabled = false;
  }
});

$allowBtn.addEventListener('click', async () => {
  const tab = await getActiveTab();
  if (!tab) return;
  const url = new URL(tab.url);
  const pattern = `*://${url.hostname}/*`;

  $allowBtn.disabled = true;
  setStatus('Waiting for permission dialog…');

  let granted = false;
  try {
    // Direct call from a popup click handler — works on Chrome, Edge,
    // Firefox, and Brave because the user gesture is co-located.
    granted = await chrome.permissions.request({ origins: [pattern] });
  } catch (e) {
    setStatus('Permission error: ' + (e?.message || e), 'error');
    $allowBtn.disabled = false;
    return;
  }

  if (!granted) {
    setStatus('Permission denied.', 'error');
    $allowBtn.disabled = false;
    return;
  }

  // Tell background to record the opt-in and register a dynamic content
  // script so future visits auto-inject without this popup. Inject right
  // now too so the user sees the tracker before the popup closes.
  chrome.runtime.sendMessage({ type: 'anisync:register-site', pattern }, async (resp) => {
    try { await injectContentScript(tab.id); } catch {}
    if (resp?.ok) {
      setStatus('Allowed — auto-tracking ' + url.hostname + '.', 'ok');
    } else {
      // Permission was granted but registration failed — the activeTab
      // injection above still works for this session, so the user gets
      // tracking even though future visits won't auto-inject.
      setStatus('Allowed for this session: ' + (resp?.error || 'register failed'), 'error');
    }
    setTimeout(() => window.close(), 600);
  });
});

$openOptions.addEventListener('click', (e) => {
  e.preventDefault();
  chrome.runtime.openOptionsPage();
});

(async () => {
  $version.textContent = 'v' + chrome.runtime.getManifest().version;
  const tab = await getActiveTab();
  await refreshUi(tab);
})();
